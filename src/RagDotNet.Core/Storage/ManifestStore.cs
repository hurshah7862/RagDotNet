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
        StoreCollections.Open(store, options.CollectionName + "_documents", RecordDefinitions.Manifests(options), options);

    public Task EnsureExistsAsync(CancellationToken ct) => StoreCollections.EnsureExistsAsync(_collection, options, ct);

    public async Task<ManifestRecord?> GetAsync(string partitionKey, string documentId, CancellationToken ct)
    {
        var key = ConvertKey(ChunkKeys.Document(partitionKey, documentId));
        var record = await _collection.GetAsync(key, new RecordRetrievalOptions { IncludeVectors = false }, ct).ConfigureAwait(false);
        return record is null ? null : ManifestRecord.FromDynamic(record, options);
    }

    public Task UpsertAsync(ManifestRecord m, CancellationToken ct) =>
        _collection.UpsertAsync(m.ToDynamic(options.KeyType, options.Metadata), ct);

    public Task DeleteAsync(string partitionKey, string documentId, CancellationToken ct) =>
        _collection.DeleteAsync(ConvertKey(ChunkKeys.Document(partitionKey, documentId)), ct);

    /// <summary>
    /// Every manifest matching <paramref name="filter"/>, in ListOrder order. Pages by keyset: each page starts at the last
    /// page's highest ListOrder, skipping only the records already listed at that value. Some stores (Qdrant) emulate Skip
    /// by fetching Skip + top records, so paging by Skip alone would cost O(N^2).
    /// </summary>
    public async IAsyncEnumerable<ManifestRecord> ListAsync(
        Expression<Func<Dictionary<string, object?>, bool>> filter, [EnumeratorCancellation] CancellationToken ct)
    {
        long? last = null;
        var listedAtLast = 0;
        while (true)
        {
            var page = new List<Dictionary<string, object?>>();
            var query = new FilteredRecordRetrievalOptions<Dictionary<string, object?>>
            {
                Skip = listedAtLast,
                IncludeVectors = false,
                OrderBy = o => o.Ascending(r => r["ListOrder"]!),
            };
            await foreach (var r in _collection.GetAsync(From(filter, last), PageSize, query, ct).ConfigureAwait(false))
                page.Add(r);
            var orders = page.Select(ListOrderOf).ToList();
            foreach (var r in page)
                yield return ManifestRecord.FromDynamic(r, options);
            if (page.Count < PageSize) yield break;
            var max = orders[^1];
            var atMax = orders.Count(o => o == max);
            listedAtLast = max == last ? listedAtLast + atMax : atMax;
            last = max;
        }
    }

    private long ListOrderOf(Dictionary<string, object?> r) =>
        r.TryGetValue("ListOrder", out var v) && v is not null
            ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException(
                $"The document list '{_collection.Name}' has records without ListOrder, written by an earlier RagDotNet build. " +
                "Delete both collections and ingest the documents again; see docs/vector-stores.md.");

    private static Expression<Func<Dictionary<string, object?>, bool>> From(Expression<Func<Dictionary<string, object?>, bool>> filter, long? last)
    {
        if (last is null) return filter;
        var r = filter.Parameters[0];
        var atOrAfter = Expression.GreaterThanOrEqual(RecordFields.Read(r, "ListOrder", typeof(long)), Expression.Constant(last.Value));
        return Expression.Lambda<Func<Dictionary<string, object?>, bool>>(Expression.AndAlso(filter.Body, atOrAfter), r);
    }

    /// <summary>Whether any manifest is not on <paramref name="configured"/> (a null stored id counts), with that id (null coerced to empty). Reads the raw field, so it works under any partition schema.</summary>
    public async Task<(bool Found, string StoredId)> FindOtherModelAsync(string configured, CancellationToken ct)
    {
        await foreach (var r in _collection.GetAsync(m => (string)m["EmbeddingModelId"]! != configured && (int)m["ChunkCount"]! >= 0, 1, new FilteredRecordRetrievalOptions<Dictionary<string, object?>> { IncludeVectors = false }, ct).ConfigureAwait(false))
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

    /// <summary>The reindex marker, or null when no reindex is in progress.</summary>
    public async Task<ReindexMarker?> GetMarkerAsync(CancellationToken ct)
    {
        var r = await _collection.GetAsync(ConvertKey(ChunkKeys.ReindexMarkerKey), new RecordRetrievalOptions { IncludeVectors = false }, ct).ConfigureAwait(false);
        if (r is null) return null;
        return new ReindexMarker(
            r.TryGetValue("EmbeddingModelId", out var model) ? model as string ?? "" : "",
            r.TryGetValue("SourceName", out var owner) ? owner as string ?? "" : "",
            r.TryGetValue("UpdatedAtUnixMs", out var beat) && beat is not null ? Convert.ToInt64(beat, System.Globalization.CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>Writes the reindex marker: a manifest-shaped record with ChunkCount -1, so document listings skip it. Metadata fields stay null.</summary>
    public Task UpsertMarkerAsync(ReindexMarker marker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var d = new Dictionary<string, object?>
        {
            ["Key"] = ConvertKey(ChunkKeys.ReindexMarkerKey),
            ["DocumentId"] = ChunkKeys.ReindexMarkerKey,
            ["Partition"] = ChunkKeys.ReindexMarkerKey,
            ["ContentHash"] = "reindex-marker",
            ["ChunkingHash"] = "",
            ["EmbeddingModelId"] = marker.TargetModelId,
            ["ChunkCount"] = -1,
            ["SourceName"] = marker.OwnerId,
            ["UpdatedAtUnixMs"] = marker.HeartbeatUnixMs,
            ["ContentStorage"] = options.ContentStorage.ToString(),
            ["MetadataHash"] = "",
            ["ChunkHighWater"] = 0,
            ["ListOrder"] = ListOrder.Of(ChunkKeys.ReindexMarkerKey),
            ["Placeholder"] = new ReadOnlyMemory<float>([1f]),
        };
        return _collection.UpsertAsync(d, ct);
    }

    /// <summary>Removes the reindex marker; does nothing when there is none.</summary>
    public Task DeleteMarkerAsync(CancellationToken ct) =>
        _collection.DeleteAsync(ConvertKey(ChunkKeys.ReindexMarkerKey), ct);

    private object ConvertKey(string key) => options.KeyType == RagKeyType.Guid ? ChunkKeys.ToGuid(key) : key;
}
