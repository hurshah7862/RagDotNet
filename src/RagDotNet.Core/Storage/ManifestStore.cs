using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using RagDotNet.Content;
using RagDotNet.Configuration;

namespace RagDotNet.Storage;

internal sealed class ManifestStore(VectorStore store, RagOptions options)
{
    private const int PageSize = 100;

    private readonly VectorStoreCollection<object, Dictionary<string, object?>> _collection =
        store.GetDynamicCollection(options.CollectionName + "_documents", RecordDefinitions.Manifests(options));

    public Task EnsureExistsAsync(CancellationToken ct) => _collection.EnsureCollectionExistsAsync(ct);

    public async Task<ManifestRecord?> GetAsync(string partitionKey, string documentId, CancellationToken ct)
    {
        var key = ConvertKey(ChunkKeys.Document(partitionKey, documentId));
        var record = await _collection.GetAsync(key, new RecordRetrievalOptions { IncludeVectors = false }, ct).ConfigureAwait(false);
        return record is null ? null : ManifestRecord.FromDynamic(record, options);
    }

    public Task UpsertAsync(ManifestRecord m, CancellationToken ct) =>
        _collection.UpsertAsync(m.ToDynamic(options.KeyType), ct);

    public Task DeleteAsync(string partitionKey, string documentId, CancellationToken ct) =>
        _collection.DeleteAsync(ConvertKey(ChunkKeys.Document(partitionKey, documentId)), ct);

    public async IAsyncEnumerable<ManifestRecord> ListAsync(
        Expression<Func<Dictionary<string, object?>, bool>> filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var skip = 0;
        while (true)
        {
            var page = new List<Dictionary<string, object?>>();
            var query = new FilteredRecordRetrievalOptions<Dictionary<string, object?>>
            {
                Skip = skip,
                IncludeVectors = false,
                OrderBy = o => o.Ascending(r => r["Key"]!),
            };
            await foreach (var r in _collection.GetAsync(filter, PageSize, query, ct).ConfigureAwait(false))
                page.Add(r);
            foreach (var r in page)
                yield return ManifestRecord.FromDynamic(r, options);
            if (page.Count < PageSize) yield break;
            skip += PageSize;
        }
    }

    public async Task<ManifestRecord?> FindFirstAsync(Expression<Func<Dictionary<string, object?>, bool>> filter, CancellationToken ct)
    {
        await foreach (var r in _collection.GetAsync(filter, 1, new FilteredRecordRetrievalOptions<Dictionary<string, object?>> { IncludeVectors = false }, ct).ConfigureAwait(false))
            return ManifestRecord.FromDynamic(r, options);
        return null;
    }

    /// <summary>Whether any manifest matches; does not materialize the record, so it works on records written under a different partition schema.</summary>
    public async Task<bool> AnyAsync(Expression<Func<Dictionary<string, object?>, bool>> filter, CancellationToken ct)
    {
        await foreach (var _ in _collection.GetAsync(filter, 1, new FilteredRecordRetrievalOptions<Dictionary<string, object?>> { IncludeVectors = false }, ct).ConfigureAwait(false))
            return true;
        return false;
    }

    /// <summary>Whether any manifest is not on <paramref name="configured"/> (a null stored id counts), with that id (null coerced to empty). Reads the raw field, so it works under any partition schema.</summary>
    public async Task<(bool Found, string StoredId)> FindOtherModelAsync(string configured, CancellationToken ct)
    {
        await foreach (var r in _collection.GetAsync(m => (string)m["EmbeddingModelId"]! != configured, 1, new FilteredRecordRetrievalOptions<Dictionary<string, object?>> { IncludeVectors = false }, ct).ConfigureAwait(false))
            return (true, r.TryGetValue("EmbeddingModelId", out var id) ? id as string ?? "" : "");
        return (false, "");
    }

    /// <summary>Whether any manifest was written in a mode other than <paramref name="configured"/>, with that mode's name. Reads the raw field.</summary>
    public async Task<(bool Found, string Stored)> FindOtherContentModeAsync(ContentStorageMode configured, CancellationToken ct)
    {
        var name = configured.ToString();
        await foreach (var r in _collection.GetAsync(m => (string)m["ContentStorage"]! != name, 1, new FilteredRecordRetrievalOptions<Dictionary<string, object?>> { IncludeVectors = false }, ct).ConfigureAwait(false))
            return (true, r.TryGetValue("ContentStorage", out var v) ? v as string ?? "" : "");
        return (false, "");
    }

    private object ConvertKey(string key) => options.KeyType == RagKeyType.Guid ? ChunkKeys.ToGuid(key) : key;
}
