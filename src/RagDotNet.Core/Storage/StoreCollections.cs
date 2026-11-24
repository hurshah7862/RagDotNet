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
    /// document list, before anything is written. A store that cannot be reached fails at the existence check, unchanged.
    /// If creation or the search rejects the settings, the collections created here are removed again and the error
    /// names the settings; collections that already existed are never touched.
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
        catch (Exception e) when (e is NotSupportedException or ArgumentException or VectorStoreException)
        {
            foreach (var collection in created) await TryDeleteAsync(collection).ConfigureAwait(false);
            throw Rejected(failing.Name, options, e);
        }
    }

    private static async Task TryDeleteAsync(VectorStoreCollection<object, Dictionary<string, object?>> collection)
    {
        try
        {
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
