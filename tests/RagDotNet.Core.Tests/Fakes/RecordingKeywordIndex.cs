using System.Collections.Concurrent;
using RagDotNet.Keyword;

namespace RagDotNet.Core.Tests.Fakes;

/// <summary>An in-memory keyword index that records calls and can fail or report a wrong count on request.</summary>
internal sealed class RecordingKeywordIndex : IKeywordIndex
{
    private readonly InMemoryKeywordIndex _inner = new();

    public string Name => "recording";

    /// <summary>Calls in order, such as "upsert a:0,a:1", "delete a:2", "search q", "count", "rebuild 3".</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Every key currently held.</summary>
    public ConcurrentDictionary<string, KeywordChunk> Chunks { get; } = new();

    public Func<IReadOnlyList<KeywordChunk>, Task>? BeforeUpsert { get; set; }

    public Func<IReadOnlyCollection<string>, Task>? BeforeDelete { get; set; }

    public bool FailNextUpsert { get; set; }

    /// <summary>When set, CountAsync reports this instead of the true count.</summary>
    public long? CountOverride { get; set; }

    public int RebuildCalls;

    public int SearchCalls;

    public async Task UpsertAsync(IReadOnlyList<KeywordChunk> chunks, CancellationToken cancellationToken)
    {
        if (BeforeUpsert is { } before) await before(chunks);
        if (FailNextUpsert)
        {
            FailNextUpsert = false;
            throw new InvalidOperationException("keyword upsert failed");
        }

        Calls.Enqueue("upsert " + string.Join(",", chunks.Select(c => c.ChunkKey)));
        foreach (var c in chunks) Chunks[c.ChunkKey] = c;
        await _inner.UpsertAsync(chunks, cancellationToken);
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> chunkKeys, CancellationToken cancellationToken)
    {
        if (BeforeDelete is { } before) await before(chunkKeys);
        Calls.Enqueue("delete " + string.Join(",", chunkKeys));
        foreach (var k in chunkKeys) Chunks.TryRemove(k, out _);
        await _inner.DeleteAsync(chunkKeys, cancellationToken);
    }

    public Task<IReadOnlyList<KeywordHit>> SearchAsync(string query, KeywordFilter filter, int top, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref SearchCalls);
        Calls.Enqueue("search " + query);
        return _inner.SearchAsync(query, filter, top, cancellationToken);
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken) =>
        CountOverride ?? await _inner.CountAsync(cancellationToken);

    public async Task RebuildAsync(IAsyncEnumerable<KeywordChunk> source, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RebuildCalls);
        var all = new List<KeywordChunk>();
        await foreach (var c in source.WithCancellation(cancellationToken)) all.Add(c);
        Calls.Enqueue("rebuild " + all.Count);
        Chunks.Clear();
        foreach (var c in all) Chunks[c.ChunkKey] = c;
        CountOverride = null;
        await _inner.RebuildAsync(ToAsync(all), cancellationToken);
    }

    private static async IAsyncEnumerable<KeywordChunk> ToAsync(IEnumerable<KeywordChunk> items)
    {
        foreach (var i in items)
        {
            await Task.Yield();
            yield return i;
        }
    }
}
