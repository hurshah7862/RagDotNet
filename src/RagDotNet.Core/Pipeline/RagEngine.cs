using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RagDotNet.Access;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Diagnostics;
using RagDotNet.Embedding;
using RagDotNet.Ingestion;
using RagDotNet.Readers;
using RagDotNet.Retrieval;
using RagDotNet.Storage;

namespace RagDotNet.Pipeline;

/// <summary>Which keyword search runs alongside vector search.</summary>
internal enum KeywordPath
{
    /// <summary>Vector-only.</summary>
    None,

    /// <summary>The vector store's own hybrid search.</summary>
    Native,

    /// <summary>The registered <see cref="Keyword.IKeywordIndex"/>.</summary>
    Index,
}

internal sealed class RagEngine
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public RagEngine(
        RagOptions options,
        ReaderSelector readers,
        ChunkerRegistry chunkers,
        TokenCounter tokens,
        string tokenizerName,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ChunkStore chunks,
        ManifestStore manifests,
        ILogger logger,
        IChunkContentStore? contentStore = null,
        TimeProvider? time = null)
    {
        Time = time ?? TimeProvider.System;
        if (options.ContentStorage == ContentStorageMode.External && contentStore is null)
            throw new NotSupportedException("External content storage requires a content store. Call UseContentStore().");
        ContentStore = options.ContentStorage == ContentStorageMode.External ? contentStore : null;
        Options = options;
        Readers = readers;
        Chunkers = chunkers;
        Tokens = tokens;
        TokenizerName = tokenizerName;
        Embedder = embedder;
        Chunks = chunks;
        Manifests = manifests;
        Logger = logger;
        EmbeddingModelId = options.EmbeddingModelId
            ?? embedder.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId
            ?? "unknown";
        Scope = new ScopeResolver(options);
        ModelGuard = new EmbeddingModelGuard(this);
        Ingestor = new Ingestor(this);
        Retriever = new Retriever(this);
        QueryPlanner = new QueryPlanner(options);
    }

    public QueryPlanner QueryPlanner { get; }

    /// <summary>The keyword index for hybrid search, if one is registered (the in-memory index is, by default).</summary>
    public Keyword.IKeywordIndex? KeywordIndex { get; init; }

    /// <summary>The reranker, if one is registered.</summary>
    public IReranker? Reranker { get; init; }

    private volatile KeywordPath _keywordPath;

    /// <summary>Which keyword search runs with vector search; decided by <see cref="EnsureReadyAsync"/>.</summary>
    public KeywordPath KeywordPath => _keywordPath;

    /// <summary>True when ingest and delete keep the keyword index in step.</summary>
    public bool WritesKeywordIndex => KeywordPath == KeywordPath.Index;

    /// <summary>Held shared by ingest and delete around their keyword writes and manifest commits, exclusively by rebuilds.</summary>
    public Keyword.KeywordSyncGate KeywordSync { get; } = new();

    /// <summary>The per-image OCR provider, if one is registered.</summary>
    public Ocr.IOcrProvider? OcrProvider { get; init; }

    /// <summary>The whole-document OCR provider, if one is registered.</summary>
    public Ocr.IDocumentOcrProvider? DocumentOcrProvider { get; init; }

    /// <summary>Page renderers for scanned pages.</summary>
    public IReadOnlyList<Ocr.IPageRenderer> PageRenderers { get; init; } = [];

    /// <summary>Reads documents and resolves their OCR candidates with the registered providers.</summary>
    public Ocr.OcrStage Ocr => _ocr ??= new Ocr.OcrStage(OcrProvider, DocumentOcrProvider, PageRenderers, Options.Ocr);

    private Ocr.OcrStage? _ocr;

    public EmbeddingModelGuard ModelGuard { get; }

    /// <summary>The clock for model check expiry and reindex heartbeats.</summary>
    public TimeProvider Time { get; }

    public Retriever Retriever { get; }

    public ScopeResolver Scope { get; }

    public RagOptions Options { get; }

    public ReaderSelector Readers { get; }

    public ChunkerRegistry Chunkers { get; }

    public TokenCounter Tokens { get; }

    public string TokenizerName { get; }

    public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; }

    public ChunkStore Chunks { get; }

    public ManifestStore Manifests { get; }

    public ILogger Logger { get; }

    /// <summary>The external content store; null in <see cref="ContentStorageMode.VectorStore"/> mode.</summary>
    public IChunkContentStore? ContentStore { get; }

    private long _missingContent;

    /// <summary>Chunks dropped because the content store had no text. Also reported as the rag.content.missing counter.</summary>
    public long MissingContentCount => Interlocked.Read(ref _missingContent);

    public void RecordMissingContent(int count)
    {
        Interlocked.Add(ref _missingContent, count);
        RagTelemetry.ContentMissing.Add(count);
    }

    public string EmbeddingModelId { get; }

    public Ingestor Ingestor { get; }

    /// <summary>Serializes ingest and delete of the same document within this process, across every pipeline over this engine.</summary>
    public DocumentLocks DocumentLocks { get; } = new();

    /// <summary>Identifies this engine as the owner of a reindex marker; a rerun in the same process resumes its own marker.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Refuses a second concurrent reindex in this process; taken with a zero timeout.</summary>
    public SemaphoreSlim ReindexGate { get; } = new(1, 1);

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
            var (path, detail) = ChooseKeywordPath();
            Logger.LogInformation("Keyword search: {Path} ({Detail})", path.ToString().ToLowerInvariant(), detail);
            if (path == KeywordPath.Index) await CheckKeywordIndexAsync(cancellationToken).ConfigureAwait(false);
            _keywordPath = path;
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// The native hybrid call threw <see cref="NotSupportedException"/>: switches to the keyword index (building it, since
    /// nothing was written to it) or to vector-only, for the rest of the process. Logs a warning once.
    /// </summary>
    public async Task SwitchFromNativeAsync(Exception error, CancellationToken ct)
    {
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_keywordPath != KeywordPath.Native) return;
            var next = KeywordIndex is null ? KeywordPath.None : KeywordPath.Index;
            Logger.LogWarning(
                "The vector store's hybrid search failed with NotSupportedException ({Message}); keyword search now uses {Path} for the rest of this process.",
                error.Message, next == KeywordPath.Index ? $"the {KeywordIndex!.Name} keyword index" : "nothing (vector-only)");
            // Exclusive across the rebuild and the switch: an ingest either committed before the rebuild read the
            // manifests, or writes the index after the switch.
            using (await KeywordSync.EnterExclusiveAsync(ct).ConfigureAwait(false))
            {
                if (next == KeywordPath.Index) await RebuildKeywordIndexCoreAsync(ct).ConfigureAwait(false);
                _keywordPath = next;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private (KeywordPath Path, string Detail) ChooseKeywordPath()
    {
        if (Options.Hybrid == HybridMode.Off) return (KeywordPath.None, "Hybrid is Off");
        if (ContentStore is null && Chunks.Hybrid is not null)
            return (KeywordPath.Native, KeywordIndex is null
                ? "the vector store's hybrid search"
                : $"the vector store's hybrid search; the {KeywordIndex.Name} keyword index is not used");
        if (KeywordIndex is not null) return (KeywordPath.Index, $"the {KeywordIndex.Name} keyword index");
        return (KeywordPath.None, "the vector store has no native hybrid search and no keyword index is registered");
    }

    /// <summary>Rebuilds the keyword index from the store when its chunk count differs from the manifests'.</summary>
    private async Task CheckKeywordIndexAsync(CancellationToken ct)
    {
        var index = KeywordIndex!;
        long expected = 0;
        await foreach (var m in Manifests.ListAsync(r => (int)r["ChunkCount"]! >= 0, ct).ConfigureAwait(false))
            expected += m.ChunkCount;
        var actual = await index.CountAsync(ct).ConfigureAwait(false);
        if (actual == expected) return;

        // The in-memory index always starts empty; building it is expected, not a sign of drift.
        if (index is not Keyword.InMemoryKeywordIndex || actual != 0)
            Logger.LogWarning(
                "The {Index} keyword index holds {IndexCount} chunks but the documents have {ChunkCount}; rebuilding it.", index.Name, actual, expected);
        using (await KeywordSync.EnterExclusiveAsync(ct).ConfigureAwait(false))
            await RebuildKeywordIndexCoreAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Rebuilds the keyword index from the vector store (or the content store, for text). For IRagAdmin.</summary>
    public async Task RebuildKeywordIndexAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        if (KeywordPath != KeywordPath.Index)
            throw new InvalidOperationException(
                $"The keyword index is not in use: keyword search is '{KeywordPath.ToString().ToLowerInvariant()}' ({ChooseKeywordPath().Detail}).");
        using (await KeywordSync.EnterExclusiveAsync(ct).ConfigureAwait(false))
            await RebuildKeywordIndexCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task RebuildKeywordIndexCoreAsync(CancellationToken ct)
    {
        var index = KeywordIndex!;
        var started = Stopwatch.GetTimestamp();
        var count = 0;
        await index.RebuildAsync(Counted(KeywordSourceAsync(ct)), ct).ConfigureAwait(false);
        Logger.LogInformation(
            "Built the {Index} keyword index: {ChunkCount} chunks in {ElapsedMs} ms.", index.Name, count, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        async IAsyncEnumerable<Keyword.KeywordChunk> Counted(IAsyncEnumerable<Keyword.KeywordChunk> source)
        {
            await foreach (var c in source.ConfigureAwait(false))
            {
                count++;
                yield return c;
            }
        }
    }

    /// <summary>Every chunk the manifests list, with its text from the record or the content store, in batches of 100 keys.</summary>
    private async IAsyncEnumerable<Keyword.KeywordChunk> KeywordSourceAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var batch = new List<(ManifestRecord Manifest, string Key)>(KeywordBatchSize);
        await foreach (var m in Manifests.ListAsync(r => (int)r["ChunkCount"]! >= 0, ct).ConfigureAwait(false))
        {
            for (var i = 0; i < m.ChunkCount; i++)
            {
                batch.Add((m, ChunkKeys.Chunk(m.Partition, m.DocumentId, i)));
                if (batch.Count < KeywordBatchSize) continue;
                foreach (var c in await FetchKeywordChunksAsync(batch, ct).ConfigureAwait(false)) yield return c;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            foreach (var c in await FetchKeywordChunksAsync(batch, ct).ConfigureAwait(false)) yield return c;
    }

    private const int KeywordBatchSize = 100;

    private async Task<List<Keyword.KeywordChunk>> FetchKeywordChunksAsync(List<(ManifestRecord Manifest, string Key)> batch, CancellationToken ct)
    {
        var keys = batch.Select(b => b.Key).ToList();

        // Headings come from the vector records in both modes; text from the records, or from the content store.
        var headings = new Dictionary<string, string>(StringComparer.Ordinal);
        var recordTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in await Chunks.GetAsync(keys, includeVectors: false, ct).ConfigureAwait(false))
        {
            var key = ChunkKeys.Chunk((string)r["Partition"]!, (string)r["DocumentId"]!, Convert.ToInt32(r["ChunkIndex"], System.Globalization.CultureInfo.InvariantCulture));
            if (r.TryGetValue("HeadingPath", out var h) && h is string heading) headings[key] = heading;
            if (r.TryGetValue("Text", out var t) && t is string text) recordTexts[key] = text;
        }

        var texts = ContentStore is { } contentStore
            ? await contentStore.GetAsync(keys, ct).ConfigureAwait(false)
            : recordTexts;

        var result = new List<Keyword.KeywordChunk>(batch.Count);
        foreach (var (m, key) in batch)
        {
            if (!texts.TryGetValue(key, out var text)) continue;
            var path = headings.TryGetValue(key, out var h) && h.Length > 0 ? h.Split(" > ") : [];
            result.Add(new Keyword.KeywordChunk(key, m.DocumentId, m.Partition, Chunking.EmbeddingText.For(path, text), m.Metadata));
        }

        return result;
    }
}
