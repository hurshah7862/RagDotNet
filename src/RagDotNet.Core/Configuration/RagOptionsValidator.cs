using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagDotNet.Chunking;
using RagDotNet.Content;

namespace RagDotNet.Configuration;

/// <summary>Records which optional services were registered, so validation never resolves scoped services from the root.</summary>
internal sealed class RagRegistrations
{
    public bool HasContentStore { get; set; }
}

/// <summary>Validates <see cref="RagOptions"/> at startup (dependency injection) and at build time (builder).</summary>
internal sealed class RagOptionsValidator(
    RagRegistrations registrations, IServiceProviderIsService serviceCheck, IEnumerable<IChunker> appChunkers) : IValidateOptions<RagOptions>
{
    internal const string ExternalHybridNeedsIndex =
        "Hybrid = On with External content storage requires a keyword index, because the vector store holds no text. Register one with AddInMemoryKeywordIndex() or AddLuceneKeywordIndex(), or set Hybrid to Auto or Off.";

    public ValidateOptionsResult Validate(string? name, RagOptions options) =>
        Validate(options, registrations.HasContentStore || serviceCheck.IsService(typeof(IChunkContentStore)), appChunkers.ToList(),
            serviceCheck.IsService(typeof(Keyword.IKeywordIndex)));

    public static ValidateOptionsResult Validate(RagOptions options, bool hasContentStore, IReadOnlyList<IChunker> appChunkers, bool hasKeywordIndex = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.CollectionName)) failures.Add("CollectionName must not be empty.");
        if (options.EmbeddingDimensions <= 0) failures.Add("EmbeddingDimensions must be greater than zero.");
        if (options.EmbeddingBatchSize < 1) failures.Add("EmbeddingBatchSize must be at least 1.");
        if (options.IngestParallelism < 1) failures.Add("IngestParallelism must be at least 1.");
        if (options.MaxContextTokens < 100) failures.Add("MaxContextTokens must be at least 100.");
        if (options.MaxHistoryMessages < 0) failures.Add("MaxHistoryMessages must not be negative.");
        if (options.QueryTransformers is null) failures.Add("QueryTransformers must not be null.");
        if (options.ModelCheckInterval < TimeSpan.Zero) failures.Add("ModelCheckInterval must not be negative.");
        // Reindex waits one interval while holding its marker; it must stay well inside the 5-minute takeover window.
        if (options.ModelCheckInterval > TimeSpan.FromMinutes(1)) failures.Add("ModelCheckInterval must not exceed 1 minute.");
        if (options.RerankCandidates is < 1 or > 100) failures.Add("RerankCandidates must be between 1 and 100.");
        if (options.HybridCandidates is < 1 or > 100) failures.Add("HybridCandidates must be between 1 and 100.");
        if (options.Ocr is null) failures.Add("Ocr must not be null.");
        else
        {
            if (options.Ocr.MinImageSize < 0) failures.Add("Ocr.MinImageSize must not be negative.");
            if (options.Ocr.MinConfidence is < 0 or > 1 || double.IsNaN(options.Ocr.MinConfidence)) failures.Add("Ocr.MinConfidence must be between 0 and 1.");
            if (options.Ocr.PageDpi is < 72 or > 1200) failures.Add("Ocr.PageDpi must be between 72 and 1200.");
            if (options.Ocr.ChunkMaxTokens is < 16) failures.Add("Ocr.ChunkMaxTokens must be at least 16 when set.");
        }

        foreach (var key in options.PartitionKeys)
        {
            if (!options.Metadata.Fields.TryGetValue(key, out var type))
                failures.Add($"Partition key '{key}' is not declared in Metadata.");
            else if (type != typeof(string))
                failures.Add($"Partition key '{key}' must be of type string but is {type.Name}.");
        }

        var chunkerNames = new HashSet<string>(StringComparer.Ordinal) { Chunkers.StructureAware, Chunkers.FixedSize, Chunkers.Semantic };
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

        if (hasContentStore && options.ContentStorage != ContentStorageMode.External)
            failures.Add("A content store is registered but ContentStorage is VectorStore. Use UseExternalContent<T>() / UseContentStore(), or set ContentStorage = External.");

        if (options.ContentStorage == ContentStorageMode.External && !hasContentStore)
            failures.Add("External content storage requires a content store. Register one with UseExternalContent<T>() or UseContentStore().");

        if (options.Hybrid == Retrieval.HybridMode.On && options.ContentStorage == ContentStorageMode.External && !hasKeywordIndex)
            failures.Add(ExternalHybridNeedsIndex);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
