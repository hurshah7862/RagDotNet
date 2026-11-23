using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Filtering;

namespace RagDotNet.Storage;

internal static class RecordDefinitions
{
    public static VectorStoreCollectionDefinition Chunks(RagOptions o)
    {
        ValidateDistance(o.DistanceFunction);
        var props = new List<VectorStoreProperty>
        {
            new VectorStoreKeyProperty("Key", KeyClrType(o.KeyType)),
            new VectorStoreDataProperty("DocumentId", typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty("Partition", typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty("ChunkIndex", typeof(int)),
        };
        if (o.ContentStorage != ContentStorageMode.External)
            props.Add(new VectorStoreDataProperty("Text", typeof(string)) { IsFullTextIndexed = true });
        props.Add(new VectorStoreDataProperty("ContentHash", typeof(string)));
        props.Add(new VectorStoreDataProperty("ChunkingHash", typeof(string)));
        props.Add(new VectorStoreDataProperty("EmbeddingModelId", typeof(string)) { IsIndexed = true });
        props.Add(new VectorStoreDataProperty("PageStart", typeof(int?)));
        props.Add(new VectorStoreDataProperty("PageEnd", typeof(int?)));
        props.Add(new VectorStoreDataProperty("HeadingPath", typeof(string)));
        props.Add(new VectorStoreDataProperty("SourceName", typeof(string)));
        foreach (var (name, type) in o.Metadata.Fields)
            props.Add(new VectorStoreDataProperty(MetaName(name), StoredType(type)) { IsIndexed = true });
        props.Add(new VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), o.EmbeddingDimensions)
        {
            DistanceFunction = o.DistanceFunction,
        });
        return new VectorStoreCollectionDefinition { Properties = props };
    }

    public static VectorStoreCollectionDefinition Manifests(RagOptions o)
    {
        ValidateDistance(o.DistanceFunction);
        var props = new List<VectorStoreProperty>
        {
            new VectorStoreKeyProperty("Key", KeyClrType(o.KeyType)),
            new VectorStoreDataProperty("DocumentId", typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty("Partition", typeof(string)) { IsIndexed = true },
        };
        foreach (var (name, type) in o.Metadata.Fields)
            props.Add(new VectorStoreDataProperty(MetaName(name), StoredType(type)) { IsIndexed = true });
        props.Add(new VectorStoreDataProperty("ContentHash", typeof(string)));
        props.Add(new VectorStoreDataProperty("ChunkingHash", typeof(string)));
        props.Add(new VectorStoreDataProperty("EmbeddingModelId", typeof(string)) { IsIndexed = true });
        props.Add(new VectorStoreDataProperty("ChunkCount", typeof(int)) { IsIndexed = true });
        props.Add(new VectorStoreDataProperty("SourceName", typeof(string)));
        props.Add(new VectorStoreDataProperty("UpdatedAtUnixMs", typeof(long)));
        props.Add(new VectorStoreDataProperty("ContentStorage", typeof(string)));
        props.Add(new VectorStoreDataProperty("MetadataHash", typeof(string)));
        props.Add(new VectorStoreDataProperty("ChunkHighWater", typeof(int)));
        // The document list pages by this, not the key: see ListOrder.
        props.Add(new VectorStoreDataProperty("ListOrder", typeof(long)) { IsIndexed = true });
        // The placeholder is never searched; it follows the configured distance so a store sees one setting for both collections.
        props.Add(new VectorStoreVectorProperty("Placeholder", typeof(ReadOnlyMemory<float>), 1)
        {
            DistanceFunction = o.DistanceFunction,
        });
        return new VectorStoreCollectionDefinition { Properties = props };
    }

    internal static string MetaName(string field) => RagFilterTranslator.MetaPrefix + field;

    /// <summary>
    /// A metadata field's type in the store: value types become nullable, because a document may omit the field (a store
    /// that maps <c>int</c> to a NOT NULL column would reject it).
    /// </summary>
    internal static Type StoredType(Type declared) =>
        declared.IsValueType && Nullable.GetUnderlyingType(declared) is null ? typeof(Nullable<>).MakeGenericType(declared) : declared;

    private static Type KeyClrType(RagKeyType t) => t == RagKeyType.Guid ? typeof(Guid) : typeof(string);

    private static void ValidateDistance(string distanceFunction)
    {
        if (distanceFunction != DistanceFunction.CosineSimilarity && distanceFunction != DistanceFunction.CosineDistance)
            throw new ArgumentException(
                $"Distance function '{distanceFunction}' is not supported. Use CosineSimilarity or CosineDistance.",
                nameof(distanceFunction));
    }
}
