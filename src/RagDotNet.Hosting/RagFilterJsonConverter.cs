using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RagDotNet.Filtering;

namespace RagDotNet.Hosting;

/// <summary>
/// Reads and writes a <see cref="RagFilter"/> tree as JSON: <c>{"op":"eq|in|contains|containsAny|and|or",…}</c>, with each value
/// tagged by its type (<c>{"string":…}</c>, <c>{"int":…}</c>, <c>{"long":…}</c>, <c>{"bool":…}</c> or <c>{"date":"…"}</c> in the
/// round-trip "O" format), so a value reads back as the type it was written as. The format is described in docs/hosting.md.
/// </summary>
public sealed class RagFilterJsonConverter : JsonConverter<RagFilter>
{
    /// <inheritdoc/>
    public override RagFilter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ReadFilter(document.RootElement);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, RagFilter value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        WriteFilter(writer, value);
    }

    internal static void WriteFilter(Utf8JsonWriter writer, RagFilter filter)
    {
        writer.WriteStartObject();
        switch (filter)
        {
            case RagFilter.EqFilter eq:
                writer.WriteString("op", "eq");
                writer.WriteString("field", eq.Field);
                writer.WritePropertyName("value");
                WriteValue(writer, eq.Value);
                break;
            case RagFilter.InFilter @in:
                writer.WriteString("op", "in");
                writer.WriteString("field", @in.Field);
                writer.WriteStartArray("values");
                foreach (var v in @in.Values) WriteValue(writer, v);
                writer.WriteEndArray();
                break;
            case RagFilter.ContainsFilter contains:
                writer.WriteString("op", "contains");
                writer.WriteString("field", contains.Field);
                writer.WriteString("value", contains.Value);
                break;
            case RagFilter.ContainsAnyFilter any:
                writer.WriteString("op", "containsAny");
                writer.WriteString("field", any.Field);
                writer.WriteStartArray("values");
                foreach (var v in any.Values) writer.WriteStringValue(v);
                writer.WriteEndArray();
                break;
            case RagFilter.AndFilter and:
                writer.WriteString("op", "and");
                WriteFilters(writer, and.Filters);
                break;
            case RagFilter.OrFilter or:
                writer.WriteString("op", "or");
                WriteFilters(writer, or.Filters);
                break;
            default:
                throw new JsonException($"RagJson cannot write a filter of type {filter.GetType().Name}.");
        }

        writer.WriteEndObject();
    }

    private static void WriteFilters(Utf8JsonWriter writer, IReadOnlyList<RagFilter> filters)
    {
        writer.WriteStartArray("filters");
        foreach (var f in filters) WriteFilter(writer, f);
        writer.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, object value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case string s:
                writer.WriteString("string", s);
                break;
            case int i:
                writer.WriteNumber("int", i);
                break;
            case long l:
                writer.WriteNumber("long", l);
                break;
            case bool b:
                writer.WriteBoolean("bool", b);
                break;
            case DateTimeOffset d:
                writer.WriteString("date", d.ToString("O", CultureInfo.InvariantCulture));
                break;
            default:
                throw new JsonException(
                    $"RagJson cannot write a filter value of type {value?.GetType().Name ?? "null"}; the supported types are string, int, long, bool and DateTimeOffset.");
        }

        writer.WriteEndObject();
    }

    internal static RagFilter ReadFilter(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("A filter must be a JSON object.");
        var op = Property(element, "op").GetString();
        return op switch
        {
            "eq" => new RagFilter.EqFilter(Field(element), ReadValue(Property(element, "value"))),
            "in" => new RagFilter.InFilter(Field(element), Array(element, "values").Select(ReadValue).ToArray()),
            "contains" => new RagFilter.ContainsFilter(Field(element), String(Property(element, "value"), "value")),
            "containsAny" => new RagFilter.ContainsAnyFilter(Field(element), Array(element, "values").Select(v => String(v, "values")).ToArray()),
            "and" => new RagFilter.AndFilter(Array(element, "filters").Select(ReadFilter).ToArray()),
            "or" => new RagFilter.OrFilter(Array(element, "filters").Select(ReadFilter).ToArray()),
            _ => throw new JsonException($"Unknown filter op '{op}'; expected eq, in, contains, containsAny, and or or."),
        };
    }

    private static object ReadValue(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("A filter value must be an object naming its type, such as {\"string\":\"x\"}.");
        using var properties = element.EnumerateObject();
        if (!properties.MoveNext()) throw new JsonException("A filter value must name its type, such as {\"string\":\"x\"}.");
        var p = properties.Current;
        if (properties.MoveNext()) throw new JsonException("A filter value must have exactly one property, naming its type.");
        try
        {
            return p.Name switch
            {
                "string" => String(p.Value, "string"),
                "int" => p.Value.GetInt32(),
                "long" => p.Value.GetInt64(),
                "bool" => p.Value.GetBoolean(),
                "date" => DateTimeOffset.ParseExact(String(p.Value, "date"), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => throw new JsonException($"Unknown filter value type '{p.Name}'; expected string, int, long, bool or date."),
            };
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            throw new JsonException($"The filter value '{p.Value.GetRawText()}' is not a valid {p.Name}.", e);
        }
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new JsonException($"The filter is missing \"{name}\".");

    private static string Field(JsonElement element) => String(Property(element, "field"), "field");

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new JsonException($"\"{name}\" must be a string.");

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        var array = Property(element, name);
        if (array.ValueKind != JsonValueKind.Array) throw new JsonException($"\"{name}\" must be an array.");
        return array.EnumerateArray().ToList();
    }
}
