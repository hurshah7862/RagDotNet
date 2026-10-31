using Microsoft.Extensions.Options;
using RagDotNet.Content;

namespace RagDotNet.Configuration;

/// <summary>Records which optional services were registered, so validation never resolves scoped services from the root.</summary>
internal sealed class RagRegistrations
{
    public bool HasAccessScope { get; set; }

    public bool HasContentStore { get; set; }
}

/// <summary>Validates <see cref="RagOptions"/> at startup (dependency injection) and at build time (builder).</summary>
internal sealed class RagOptionsValidator(RagRegistrations registrations) : IValidateOptions<RagOptions>
{
    public ValidateOptionsResult Validate(string? name, RagOptions options) =>
        Validate(options, registrations.HasAccessScope, registrations.HasContentStore, requireScopeForPartitions: true);

    public static ValidateOptionsResult Validate(RagOptions options, bool hasAccessScope, bool hasContentStore, bool requireScopeForPartitions)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.CollectionName)) failures.Add("CollectionName must not be empty.");
        if (options.EmbeddingDimensions <= 0) failures.Add("EmbeddingDimensions must be greater than zero.");
        if (options.EmbeddingBatchSize < 1) failures.Add("EmbeddingBatchSize must be at least 1.");
        if (options.IngestParallelism < 1) failures.Add("IngestParallelism must be at least 1.");
        if (options.MaxContextTokens < 100) failures.Add("MaxContextTokens must be at least 100.");

        foreach (var key in options.PartitionKeys)
        {
            if (!options.Metadata.Fields.TryGetValue(key, out var type))
                failures.Add($"Partition key '{key}' is not declared in Metadata.");
            else if (type != typeof(string))
                failures.Add($"Partition key '{key}' must be of type string but is {type.Name}.");
        }

        if (requireScopeForPartitions && options.PartitionKeys.Count > 0 && !hasAccessScope)
            failures.Add($"Partition keys [{string.Join(", ", options.PartitionKeys)}] require an access scope. Register one with AddAccessScope<T>() or UseAccessScope().");

        if (options.ContentStorage == ContentStorageMode.External && !hasContentStore)
            failures.Add("External content storage requires a content store. Register one with UseExternalContent<T>() or UseContentStore().");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
