using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;

namespace RagDotNet.Storage;

internal sealed class ChunkStore(VectorStore store, RagOptions options)
{
    private readonly VectorStoreCollection<object, Dictionary<string, object?>> _collection =
        store.GetDynamicCollection(options.CollectionName, RecordDefinitions.Chunks(options));

    public Task EnsureExistsAsync(CancellationToken ct) => _collection.EnsureCollectionExistsAsync(ct);

    public Task UpsertAsync(IReadOnlyList<Dictionary<string, object?>> records, CancellationToken ct) =>
        records.Count == 0 ? Task.CompletedTask : _collection.UpsertAsync(records, ct);

    public async Task DeleteAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        var converted = keys.Select(ConvertKey).ToList();
        if (converted.Count > 0) await _collection.DeleteAsync(converted, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAsync(IEnumerable<string> keys, bool includeVectors, CancellationToken ct)
    {
        var converted = keys.Select(ConvertKey).ToList();
        var result = new List<Dictionary<string, object?>>();
        if (converted.Count == 0) return result;
        await foreach (var r in _collection.GetAsync(converted, new RecordRetrievalOptions { IncludeVectors = includeVectors }, ct).ConfigureAwait(false))
            result.Add(r);
        return result;
    }

    /// <summary>Whether any chunk matches the filter; reads no vectors.</summary>
    public async Task<bool> AnyAsync(Expression<Func<Dictionary<string, object?>, bool>> filter, CancellationToken ct)
    {
        await foreach (var _ in _collection.GetAsync(filter, 1, new FilteredRecordRetrievalOptions<Dictionary<string, object?>> { IncludeVectors = false }, ct).ConfigureAwait(false))
            return true;
        return false;
    }

    public IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> SearchAsync(
        ReadOnlyMemory<float> vector, int top, Expression<Func<Dictionary<string, object?>, bool>>? filter, CancellationToken ct) =>
        _collection.SearchAsync(vector, top, new VectorSearchOptions<Dictionary<string, object?>> { Filter = filter }, ct);

    private object ConvertKey(string key) => options.KeyType == RagKeyType.Guid ? ChunkKeys.ToGuid(key) : key;
}
