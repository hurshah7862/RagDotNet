using System.Text.Json;
using System.Text.Json.Serialization;
using RagDotNet.Access;

namespace RagDotNet.Hosting;

/// <summary>
/// Reads and writes an <see cref="AccessScope"/> as <c>{"partition":{…},"filter":…|null,"permissions":"Read, Write"}</c>; the
/// filter uses <see cref="RagFilterJsonConverter"/>'s format. <c>partition</c> and <c>permissions</c> are required, so a damaged
/// record is refused rather than read as full access.
/// </summary>
public sealed class AccessScopeJsonConverter : JsonConverter<AccessScope>
{
    /// <inheritdoc/>
    public override AccessScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("An access scope must be a JSON object.");

        if (!root.TryGetProperty("partition", out var partitionElement) || partitionElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The access scope needs a \"partition\" object.");
        var partition = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in partitionElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) throw new JsonException($"Partition value \"{p.Name}\" must be a string.");
            partition[p.Name] = p.Value.GetString()!;
        }

        var filter = root.TryGetProperty("filter", out var filterElement) && filterElement.ValueKind != JsonValueKind.Null
            ? RagFilterJsonConverter.ReadFilter(filterElement)
            : null;

        if (!root.TryGetProperty("permissions", out var permissionsElement) || permissionsElement.ValueKind != JsonValueKind.String)
            throw new JsonException("The access scope needs \"permissions\", such as \"Read, Write\".");
        if (!Enum.TryParse<RagPermissions>(permissionsElement.GetString(), out var permissions))
            throw new JsonException($"Unknown permissions '{permissionsElement.GetString()}'; expected None, Read, Write, Delete or All, or a comma-separated list.");

        return new AccessScope(partition, filter, permissions);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, AccessScope value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteStartObject("partition");
        foreach (var (key, v) in value.Partition) writer.WriteString(key, v);
        writer.WriteEndObject();
        writer.WritePropertyName("filter");
        if (value.Filter is null) writer.WriteNullValue();
        else RagFilterJsonConverter.WriteFilter(writer, value.Filter);
        writer.WriteString("permissions", value.Permissions.ToString());
        writer.WriteEndObject();
    }
}
