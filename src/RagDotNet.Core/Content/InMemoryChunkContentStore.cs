using System.Collections.Concurrent;

namespace RagDotNet.Content;

/// <summary>A thread-safe in-memory <see cref="IChunkContentStore"/>, for tests and samples.</summary>
public sealed class InMemoryChunkContentStore : IChunkContentStore
{
    private readonly ConcurrentDictionary<string, string> _texts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task UpsertAsync(IReadOnlyList<ChunkContent> chunks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var c in chunks) _texts[c.ChunkKey] = c.Text;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunkKeys);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in chunkKeys)
            if (_texts.TryGetValue(k, out var t)) result[k] = t;
        return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
    }

    /// <inheritdoc />
    public Task DeleteAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunkKeys);
        foreach (var k in chunkKeys) _texts.TryRemove(k, out _);
        return Task.CompletedTask;
    }

    /// <summary>The number of stored chunks.</summary>
    public int Count => _texts.Count;
}
