using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagDotNet.Chunking;
using RagDotNet.Content;

namespace RagDotNet.Configuration;

/// <summary>Records which optional services were registered, so validation never resolves scoped services from the root.</summary>
internal sealed class RagRegistrations
{
    public bool HasAccessScope { get; set; }

    public bool HasContentStore { get; set; }
}

/// <summary>Validates <see cref="RagOptions"/> at startup (dependency injection) and at build time (builder).</summary>
internal sealed class RagOptionsValidator(
    RagRegistrations registrations, IServiceProviderIsService serviceCheck, IEnumerable<IChunker> appChunkers) : IValidateOptions<RagOptions>
{
    public ValidateOptionsResult Validate(string? name, RagOptions options) =>
        Validate(options, registrations.HasContentStore || serviceCheck.IsService(typeof(IChunkContentStore)), appChunkers.ToList());

    public static ValidateOptionsResult Validate(RagOptions options, bool hasContentStore, IReadOnlyList<IChunker> appChunkers)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.CollectionName)) failures.Add("CollectionName must not be empty.");
        if (options.EmbeddingDimensions <= 0) failures.Add("EmbeddingDimensions must be greater than zero.");
        if (options.EmbeddingBatchSize < 1) failures.Add("EmbeddingBatchSize must be at least 1.");
        if (options.IngestParallelism < 1) failures.Add("IngestParallelism must be at least 1.");
        if (options.MaxContextTokens < 100) failures.Add("MaxContextTokens must be at least 100.");
        if (options.ModelCheckInterval < TimeSpan.Zero) failures.Add("ModelCheckInterval must not be negative.");

        foreach (var key in options.PartitionKeys)
        {
            if (!options.Metadata.Fields.TryGetValue(key, out var type))
                failures.Add($"Partition key '{key}' is not declared in Metadata.");
            else if (type != typeof(string))
                failures.Add($"Partition key '{key}' must be of type string but is {type.Name}.");
        }

        var chunkerNames = new HashSet<string>(StringComparer.Ordinal) { Chunkers.StructureAware };
        foreach (var chunker in appChunkers)
        {
            if (string.IsNullOrWhiteSpace(chunker.Name))
            {
                failures.Add($"Chunker '{chunker.GetType().Name}' must have a non-blank Name.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(chunker.Version)) failures.Add($"Chunker '{chunker.Name}' must have a non-blank Version.");
            if (!chunkerNames.Add(chunker.Name)) failures.Add($"Chunker name '{chunker.Name}' is registered more than once.");
        }

        if (!chunkerNames.Contains(options.Chunking.Chunker ?? ""))
            failures.Add($"Chunking.Chunker '{options.Chunking.Chunker}' is not a registered chunker.");
        foreach (var (contentType, chunking) in options.ChunkingByContentType)
            if (!chunkerNames.Contains(chunking.Chunker ?? ""))
                failures.Add($"ChunkingByContentType['{contentType}'].Chunker '{chunking.Chunker}' is not a registered chunker.");

        if (hasContentStore && options.ContentStorage !=ContentStorageMode.External)
            failures.Add("A content store is registered but ContentStorage is VectorStore. Use UseExternalContent<T>() / UseContentStore(), or set ContentStorage = External.");

        if (options.ContentStorage == ContentStorageMode.External && !hasContentStore)
            failures.Add("External content storage requires a content store. Register one with UseExternalContent<T>() or UseContentStore().");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
