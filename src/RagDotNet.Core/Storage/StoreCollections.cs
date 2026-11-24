using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;

namespace RagDotNet.Storage;

/// <summary>
/// Opens and creates RagDotNet's collections. A store that rejects the settings (a NotSupportedException or
/// ArgumentException, as Qdrant throws for string keys, or a VectorStoreException from a store that is reachable)
/// gets an error naming the settings to change; a store that cannot be reached keeps its own error.
/// </summary>
internal static class StoreCollections
{
    public static VectorStoreCollection<object, Dictionary<string, object?>> Open(
        VectorStore store, string name, VectorStoreCollectionDefinition definition, RagOptions options)
    {
        try
        {
            return store.GetDynamicCollection(name, definition);
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            throw Rejected(name, options, e);
        }
    }

    /// <summary>
    /// Creates whichever of the two collections is missing, then proves the settings with a one-result search on the
    /// document list, before anything is written. A store that cannot be reached, or a transient fault, keeps its own error.
    /// If creation or the search rejects the settings, the collections created here are removed again while they are
    /// still empty, and the error names the settings; collections that already existed are never touched.
    /// </summary>
    public static async Task EnsureExistsAsync(
        VectorStoreCollection<object, Dictionary<string, object?>> chunks,
        VectorStoreCollection<object, Dictionary<string, object?>> manifests,
        RagOptions options,
        CancellationToken ct)
    {
        var chunksExisted = await chunks.CollectionExistsAsync(ct).ConfigureAwait(false);
        var manifestsExisted = await manifests.CollectionExistsAsync(ct).ConfigureAwait(false);
        var created = new List<VectorStoreCollection<object, Dictionary<string, object?>>>(2);
        var failing = manifests;
        try
        {
            if (!chunksExisted)
            {
                failing = chunks;
                created.Add(chunks);
                await chunks.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
            }

            if (!manifestsExisted)
            {
                failing = manifests;
                created.Add(manifests);
                await manifests.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
            }

            failing = manifests;
            await foreach (var _ in manifests.SearchAsync(new ReadOnlyMemory<float>([1f]), 1, cancellationToken: ct).ConfigureAwait(false))
            {
            }
        }
        catch (Exception e) when ((e is NotSupportedException or ArgumentException or VectorStoreException) && !IsTransient(e))
        {
            foreach (var collection in created) await TryDeleteIfEmptyAsync(collection).ConfigureAwait(false);
            throw Rejected(failing.Name, options, e);
        }
    }

    /// <summary>
    /// A fault on the way to the store (a timeout, a dropped connection, an HTTP or socket error) is not a verdict on the
    /// settings, so it keeps its own error and nothing is deleted; startup is retried on the next call.
    /// </summary>
    private static bool IsTransient(Exception e)
    {
        for (var x = e.InnerException; x is not null; x = x.InnerException)
            if (x is TimeoutException or IOException or HttpRequestException or System.ComponentModel.Win32Exception)
                return true;
        return false;
    }

    /// <summary>
    /// Removes a collection this call created, unless it holds a record: another process may have created it between the
    /// existence check and this call's creation, and its data is never ours to delete. A collection that cannot be read
    /// is kept too.
    /// </summary>
    private static async Task TryDeleteIfEmptyAsync(VectorStoreCollection<object, Dictionary<string, object?>> collection)
    {
        try
        {
            await foreach (var _ in collection.GetAsync(r => (string)r["DocumentId"]! != "~", 1, cancellationToken: CancellationToken.None).ConfigureAwait(false))
                return;
            await collection.EnsureCollectionDeletedAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Cleanup after a rejection: the rejection is the error worth reporting.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static InvalidOperationException Rejected(string name, RagOptions o, Exception inner) =>
        new($"The vector store rejected RagDotNet's '{name}' collection (key type {o.KeyType}, distance {o.DistanceFunction}, " +
            $"{o.EmbeddingDimensions} dimensions): {inner.Message} Some stores need other settings, such as " +
            "RagOptions.KeyType or RagOptions.DistanceFunction; see docs/vector-stores.md.", inner);
}
