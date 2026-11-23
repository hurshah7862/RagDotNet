using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;

namespace RagDotNet.Storage;

/// <summary>
/// Opens and creates RagDotNet's collections. A store that rejects the definition (a NotSupportedException or
/// ArgumentException, as Qdrant throws for string keys) gets an error naming the settings to change; any other error,
/// such as a store that cannot be reached, passes through unchanged.
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

    public static async Task EnsureExistsAsync(
        VectorStoreCollection<object, Dictionary<string, object?>> collection, RagOptions options, CancellationToken ct)
    {
        try
        {
            await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            throw Rejected(collection.Name, options, e);
        }
    }

    private static InvalidOperationException Rejected(string name, RagOptions o, Exception inner) =>
        new($"The vector store rejected RagDotNet's '{name}' collection (key type {o.KeyType}, distance {o.DistanceFunction}, " +
            $"{o.EmbeddingDimensions} dimensions): {inner.Message} Some stores need other settings, for example " +
            "RagOptions.KeyType = RagKeyType.Guid; see docs/vector-stores.md.", inner);
}
