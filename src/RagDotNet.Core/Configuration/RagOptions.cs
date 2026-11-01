using Microsoft.ML.Tokenizers;
using RagDotNet.Chunking;
using RagDotNet.Content;
using RagDotNet.Filtering;
using RagDotNet.Generation;
using RagDotNet.Storage;

namespace RagDotNet.Configuration;

/// <summary>Options for the RAG pipeline.</summary>
public sealed class RagOptions
{
    /// <summary>Name of the vector store collection (manifests use a derived name).</summary>
    public string CollectionName { get; set; } = "rag";

    /// <summary>Dimensionality of the embedding vectors.</summary>
    public int EmbeddingDimensions { get; set; }

    /// <summary>Identifier of the embedding model; recorded so re-embedding can be detected.</summary>
    public string? EmbeddingModelId { get; set; }

    /// <summary>
    /// How long the embedding model check result is reused before the store is consulted again. The check looks for
    /// documents on another model and for a reindex in progress, so a running instance notices a reindex within this time.
    /// <see cref="TimeSpan.Zero"/> checks on every call; negative values and values over 1 minute fail validation. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan ModelCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Key type of vector store records.</summary>
    public RagKeyType KeyType { get; set; } = RagKeyType.String;

    /// <summary>Distance function; only CosineSimilarity and CosineDistance are accepted.</summary>
    public string DistanceFunction { get; set; } = Microsoft.Extensions.VectorData.DistanceFunction.CosineSimilarity;

    /// <summary>Declared custom metadata fields.</summary>
    public MetadataSchema Metadata { get; set; } = new();

    /// <summary>Metadata fields that partition documents (for example a tenant).</summary>
    public IList<string> PartitionKeys { get; set; } = [];

    /// <summary>Default chunking options.</summary>
    public ChunkingOptions Chunking { get; set; } = new();

    /// <summary>Chunking overrides by content type.</summary>
    public IDictionary<string, ChunkingOptions> ChunkingByContentType { get; set; } =
        new Dictionary<string, ChunkingOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of chunks embedded per request.</summary>
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>Maximum documents ingested concurrently.</summary>
    public int IngestParallelism { get; set; } = 4;

    /// <summary>Token budget for the context passed to the model.</summary>
    public int MaxContextTokens { get; set; } = 3000;

    /// <summary>
    /// How many earlier user and assistant messages of a conversation are used to condense the question and are sent to the model.
    /// The history does not count against <see cref="MaxContextTokens"/>. 0 ignores the history. Default 10.
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 10;

    /// <summary>Tokenizer used for counting tokens; a default is used when null.</summary>
    public Tokenizer? Tokenizer { get; set; }

    /// <summary>Custom prompt builder; the default is used when null.</summary>
    public IRagPromptBuilder? Prompt { get; set; }

    /// <summary>Where chunk text is stored.</summary>
    public ContentStorageMode ContentStorage { get; set; } = ContentStorageMode.VectorStore;

    /// <summary>When true, document text may appear in telemetry.</summary>
    public bool EnableSensitiveData { get; set; }

    /// <summary>Query transformers applied, in order, to every question. Empty by default. <see cref="Retrieval.RetrievalOptions.QueryTransformers"/> overrides this per call.</summary>
    public IList<Retrieval.IQueryTransformer> QueryTransformers { get; set; } = [];
}
