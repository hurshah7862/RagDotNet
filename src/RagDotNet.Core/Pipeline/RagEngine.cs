using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RagDotNet.Access;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Embedding;
using RagDotNet.Ingestion;
using RagDotNet.Readers;
using RagDotNet.Retrieval;
using RagDotNet.Storage;

namespace RagDotNet.Pipeline;

internal sealed class RagEngine
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public RagEngine(
        RagOptions options,
        ReaderSelector readers,
        IChunker chunker,
        TokenCounter tokens,
        string tokenizerName,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ChunkStore chunks,
        ManifestStore manifests,
        ILogger logger,
        IChunkContentStore? contentStore = null)
    {
        if (options.ContentStorage == ContentStorageMode.External && contentStore is null)
            throw new NotSupportedException("External content storage requires a content store. Call UseContentStore().");
        ContentStore = options.ContentStorage == ContentStorageMode.External ? contentStore : null;
        Options = options;
        Readers = readers;
        Chunker = chunker;
        Tokens = tokens;
        TokenizerName = tokenizerName;
        Embedder = embedder;
        Chunks = chunks;
        Manifests = manifests;
        Logger = logger;
        EmbeddingModelId = options.EmbeddingModelId
            ?? embedder.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId
            ?? "unknown";
        Scope = new ScopeResolver(options, manifests);
        ModelGuard = new EmbeddingModelGuard(this);
        Ingestor = new Ingestor(this);
        Retriever = new Retriever(this);
    }

    public EmbeddingModelGuard ModelGuard { get; }

    public Retriever Retriever { get; }

    public ScopeResolver Scope { get; }

    public RagOptions Options { get; }

    public ReaderSelector Readers { get; }

    public IChunker Chunker { get; }

    public TokenCounter Tokens { get; }

    public string TokenizerName { get; }

    public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; }

    public ChunkStore Chunks { get; }

    public ManifestStore Manifests { get; }

    public ILogger Logger { get; }

    /// <summary>The external content store; null in <see cref="ContentStorageMode.VectorStore"/> mode.</summary>
    public IChunkContentStore? ContentStore { get; }

    private long _missingContent;

    /// <summary>Chunks dropped because the content store had no text. Task 13 exposes it as rag.content.missing.</summary>
    public long MissingContentCount => Interlocked.Read(ref _missingContent);

    public void RecordMissingContent(int count) => Interlocked.Add(ref _missingContent, count);

    public string EmbeddingModelId { get; }

    public Ingestor Ingestor { get; }

    /// <summary>Resolves the caller's scope once for a public call.</summary>
    public ValueTask<ResolvedScope> ResolveScopeAsync(IAccessScope? scope, CancellationToken cancellationToken) =>
        Scope.ResolveAsync(scope, cancellationToken);

    /// <summary>Creates both collections once; thread-safe, and retried if it failed.</summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await Chunks.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            await Manifests.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
