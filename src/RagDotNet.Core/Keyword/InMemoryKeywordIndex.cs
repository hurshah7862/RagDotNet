using RagDotNet.Filtering;

namespace RagDotNet.Keyword;

/// <summary>
/// A keyword index held in this process's memory, ranked with BM25 (k1 = 1.2, b = 0.75). It is the default index: built
/// from the vector store when the engine starts and kept in step by ingest and delete. Another process's writes are not
/// seen until this one restarts. Thread-safe.
/// </summary>
public sealed class InMemoryKeywordIndex : IKeywordIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly ReaderWriterLockSlim _lock = new();
    private State _state = new();

    /// <inheritdoc />
    public string Name => "in-memory";

    /// <inheritdoc />
    public Task UpsertAsync(IReadOnlyList<KeywordChunk> chunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();
        var tokenized = chunks.Select(c => (Chunk: c, Terms: KeywordTokenizer.Terms(c.Text))).ToList();
        _lock.EnterWriteLock();
        try
        {
            foreach (var (chunk, terms) in tokenized) _state.Add(chunk, terms);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(IReadOnlyCollection<string> chunkKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunkKeys);
        cancellationToken.ThrowIfCancellationRequested();
        _lock.EnterWriteLock();
        try
        {
            foreach (var key in chunkKeys) _state.Remove(key);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KeywordHit>> SearchAsync(string query, KeywordFilter filter, int top, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);
        cancellationToken.ThrowIfCancellationRequested();
        var terms = KeywordTokenizer.Terms(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0) return Task.FromResult<IReadOnlyList<KeywordHit>>([]);

        var partitions = filter.Partitions is null ? null : new HashSet<string>(filter.Partitions, StringComparer.Ordinal);
        _lock.EnterReadLock();
        try
        {
            var state = _state;
            var count = state.Docs.Count;
            if (count == 0) return Task.FromResult<IReadOnlyList<KeywordHit>>([]);
            var averageLength = (double)state.TotalLength / count;
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var term in terms)
            {
                if (!state.Postings.TryGetValue(term, out var postings)) continue;
                var idf = Math.Log(1 + (count - postings.Count + 0.5) / (postings.Count + 0.5));
                foreach (var (key, tf) in postings)
                {
                    if (rejected.Contains(key)) continue;
                    var doc = state.Docs[key];
                    if (!scores.ContainsKey(key) && !Allowed(doc.Chunk, filter, partitions))
                    {
                        rejected.Add(key);
                        continue;
                    }

                    var norm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * doc.Length / averageLength));
                    scores[key] = scores.GetValueOrDefault(key) + idf * norm;
                }
            }

            IReadOnlyList<KeywordHit> hits = scores
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(top)
                .Select(kv => new KeywordHit(kv.Key, kv.Value))
                .ToList();
            return Task.FromResult(hits);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken cancellationToken)
    {
        _lock.EnterReadLock();
        try
        {
            return Task.FromResult((long)_state.Docs.Count);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public async Task RebuildAsync(IAsyncEnumerable<KeywordChunk> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var fresh = new State();
        await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            fresh.Add(chunk, KeywordTokenizer.Terms(chunk.Text));

        _lock.EnterWriteLock();
        try
        {
            _state = fresh;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static bool Allowed(KeywordChunk chunk, KeywordFilter filter, HashSet<string>? partitions)
    {
        if (filter.ExcludeCommon && chunk.Partition.Length == 0) return false;
        if (partitions is not null && !partitions.Contains(chunk.Partition)) return false;
        return RagFilterEvaluator.MatchesConverted(filter.Metadata, chunk.DocumentId, chunk.Metadata);
    }

    private sealed record Doc(KeywordChunk Chunk, int Length, string[] Terms);

    /// <summary>Postings (term → chunk key → term frequency), chunks and their lengths. Guarded by the index's lock.</summary>
    private sealed class State
    {
        public Dictionary<string, Dictionary<string, int>> Postings { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Doc> Docs { get; } = new(StringComparer.Ordinal);

        public long TotalLength { get; private set; }

        public void Add(KeywordChunk chunk, IReadOnlyList<string> terms)
        {
            Remove(chunk.ChunkKey);
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in terms) frequencies[term] = frequencies.GetValueOrDefault(term) + 1;
            foreach (var (term, tf) in frequencies)
            {
                if (!Postings.TryGetValue(term, out var postings)) Postings[term] = postings = new(StringComparer.Ordinal);
                postings[chunk.ChunkKey] = tf;
            }

            // The text is not kept: searches need only the postings, and filters only the key, partition and metadata.
            Docs[chunk.ChunkKey] = new Doc(chunk with { Text = "" }, terms.Count, [.. frequencies.Keys]);
            TotalLength += terms.Count;
        }

        public void Remove(string key)
        {
            if (!Docs.Remove(key, out var doc)) return;
            TotalLength -= doc.Length;
            foreach (var term in doc.Terms)
            {
                var postings = Postings[term];
                postings.Remove(key);
                if (postings.Count == 0) Postings.Remove(term);
            }
        }
    }
}
