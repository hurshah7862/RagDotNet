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
            props.Add(new VectorStoreDataProperty(MetaName(name), type) { IsIndexed = true });
        props.Add(new VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), o.EmbeddingDimensions)
        {
            DistanceFunction = o.DistanceFunction,
        });
        return new VectorStoreCollectionDefinition { Properties = props };
    }

    public static VectorStoreCollectionDefinition Manifests(RagOptions o)
    {
        var props = new List<VectorStoreProperty>
        {
            new VectorStoreKeyProperty("Key", KeyClrType(o.KeyType)),
            new VectorStoreDataProperty("DocumentId", typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty("Partition", typeof(string)) { IsIndexed = true },
        };
        foreach (var k in o.PartitionKeys)
            props.Add(new VectorStoreDataProperty(MetaName(k), typeof(string)) { IsIndexed = true });
        props.Add(new VectorStoreDataProperty("ContentHash", typeof(string)));
        props.Add(new VectorStoreDataProperty("ChunkingHash", typeof(string)));
        props.Add(new VectorStoreDataProperty("EmbeddingModelId", typeof(string)) { IsIndexed = true });
        props.Add(new VectorStoreDataProperty("ChunkCount", typeof(int)) { IsIndexed = true });
        props.Add(new VectorStoreDataProperty("SourceName", typeof(string)));
        props.Add(new VectorStoreDataProperty("UpdatedAtUnixMs", typeof(long)));
        props.Add(new VectorStoreDataProperty("ContentStorage", typeof(string)));
        props.Add(new VectorStoreDataProperty("MetadataHash", typeof(string)));
        props.Add(new VectorStoreDataProperty("ChunkHighWater", typeof(int)));
        props.Add(new VectorStoreVectorProperty("Placeholder", typeof(ReadOnlyMemory<float>), 1)
        {
            DistanceFunction = DistanceFunction.CosineSimilarity,
        });
        return new VectorStoreCollectionDefinition { Properties = props };
    }

    internal static string MetaName(string field) => RagFilterTranslator.MetaPrefix + field;

    private static Type KeyClrType(RagKeyType t) => t == RagKeyType.Guid ? typeof(Guid) : typeof(string);

    private static void ValidateDistance(string distanceFunction)
    {
        if (distanceFunction != DistanceFunction.CosineSimilarity && distanceFunction != DistanceFunction.CosineDistance)
            throw new ArgumentException(
                $"Distance function '{distanceFunction}' is not supported. Use CosineSimilarity or CosineDistance.",
                nameof(distanceFunction));
    }
}
