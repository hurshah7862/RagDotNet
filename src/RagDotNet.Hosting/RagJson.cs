using System.Text.Json;

namespace RagDotNet.Hosting;

/// <summary>JSON support for durable queues, which must store each job's <see cref="Access.AccessScope"/> and its <see cref="Filtering.RagFilter"/>.</summary>
public static class RagJson
{
    /// <summary>Read-only serializer options with <see cref="AccessScopeJsonConverter"/> and <see cref="RagFilterJsonConverter"/>.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { Converters = { new AccessScopeJsonConverter(), new RagFilterJsonConverter() } };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
