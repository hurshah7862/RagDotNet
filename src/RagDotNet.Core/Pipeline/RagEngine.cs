using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Content;
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
        ILogger logger)
    {
        if (options.ContentStorage == ContentStorageMode.External)
            throw new NotSupportedException("External content storage is not supported yet.");
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
        Ingestor = new Ingestor(this);
        Retriever = new Retriever(this);
    }

    public Retriever Retriever { get; }

    public RagOptions Options { get; }

    public ReaderSelector Readers { get; }

    public IChunker Chunker { get; }

    public TokenCounter Tokens { get; }

    public string TokenizerName { get; }

    public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; }

    public ChunkStore Chunks { get; }

    public ManifestStore Manifests { get; }

    public ILogger Logger { get; }

    public string EmbeddingModelId { get; }

    public Ingestor Ingestor { get; }

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
