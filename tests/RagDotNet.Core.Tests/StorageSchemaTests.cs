using Microsoft.Extensions.VectorData;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Storage;

namespace RagDotNet.Core.Tests;

public class StorageSchemaTests
{
    private static RagOptions Options(ContentStorageMode mode = ContentStorageMode.VectorStore, RagKeyType keyType = RagKeyType.String)
    {
        var o = new RagOptions { EmbeddingDimensions = 768, ContentStorage = mode, KeyType = keyType };
        o.Metadata.Add<string>("tenant");
        o.PartitionKeys.Add("tenant");
        return o;
    }

    [Fact]
    public void Chunk_keys_without_partition() => Assert.Equal("doc1:3", ChunkKeys.Chunk("", "doc1", 3));

    [Fact]
    public void Chunk_keys_with_partition_are_order_independent_and_escaped()
    {
        var p1 = ChunkKeys.Partition(new Dictionary<string, string> { ["b"] = "2", ["a"] = "x|y" });
        var p2 = ChunkKeys.Partition(new Dictionary<string, string> { ["a"] = "x|y", ["b"] = "2" });
        Assert.Equal("a=x%7Cy&b=2", p1);
        Assert.Equal(p1, p2);
        Assert.Equal("a=x%7Cy&b=2|doc:1:0", ChunkKeys.Chunk(p1, "doc:1", 0));
        Assert.Equal("a=x%7Cy&b=2|doc:1", ChunkKeys.Document(p1, "doc:1"));
        Assert.Equal("", ChunkKeys.Partition(null));
        Assert.Equal("", ChunkKeys.Partition(new Dictionary<string, string>()));
    }

    [Fact]
    public void Guid_keys_are_deterministic_and_distinct()
    {
        var a = ChunkKeys.ToGuid("doc1:0");
        Assert.Equal(a, ChunkKeys.ToGuid("doc1:0"));
        Assert.NotEqual(a, ChunkKeys.ToGuid("doc1:1"));
        var bytes = a.ToByteArray(bigEndian: true);
        Assert.Equal(8, bytes[6] >> 4);
        Assert.Equal(0b10, bytes[8] >> 6);
    }

    [Fact]
    public void Chunking_hash_changes_with_max_tokens_and_tokenizer()
    {
        var o = new ChunkingOptions();
        var h = Hashing.ChunkingHash(o, "cl100k");
        Assert.Equal(h, Hashing.ChunkingHash(o, "cl100k"));
        Assert.NotEqual(h, Hashing.ChunkingHash(o with { MaxTokens = 256 }, "cl100k"));
        Assert.NotEqual(h, Hashing.ChunkingHash(o, "other"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Hashing.ContentHash([]));
    }

    [Fact]
    public void Chunk_definition_has_meta_fields_and_dimension()
    {
        var def = RecordDefinitions.Chunks(Options());
        var meta = Assert.IsType<VectorStoreDataProperty>(def.Properties.Single(p => p.Name == "meta_tenant"));
        Assert.True(meta.IsIndexed);
        var emb = Assert.IsType<VectorStoreVectorProperty>(def.Properties.Single(p => p.Name == "Embedding"));
        Assert.Equal(768, emb.Dimensions);
        Assert.Equal(DistanceFunction.CosineSimilarity, emb.DistanceFunction);
        Assert.Contains(def.Properties, p => p.Name == "Text");
        Assert.Equal(typeof(string), def.Properties.Single(p => p.Name == "Key").Type);

        var guid = RecordDefinitions.Chunks(Options(keyType: RagKeyType.Guid));
        Assert.Equal(typeof(Guid), guid.Properties.Single(p => p.Name == "Key").Type);
    }

    [Fact]
    public void Chunk_definition_omits_text_in_external_mode()
    {
        var def = RecordDefinitions.Chunks(Options(ContentStorageMode.External));
        Assert.DoesNotContain(def.Properties, p => p.Name == "Text");
    }

    [Fact]
    public void Manifest_definition_has_partition_fields_and_placeholder()
    {
        var def = RecordDefinitions.Manifests(Options());
        Assert.True(Assert.IsType<VectorStoreDataProperty>(def.Properties.Single(p => p.Name == "meta_tenant")).IsIndexed);
        Assert.Equal(1, Assert.IsType<VectorStoreVectorProperty>(def.Properties.Single(p => p.Name == "Placeholder")).Dimensions);
    }

    [Fact]
    public void Manifest_round_trips_through_dynamic_record()
    {
        var m = new ManifestRecord("tenant=a|doc:1","doc:1", "tenant=a", new Dictionary<string, string> { ["tenant"] = "a" },
            "ch", "kh", "model", 7, "file.md", 1234567890123L, ContentStorageMode.External);
        foreach (var kt in new[] { RagKeyType.String, RagKeyType.Guid })
        {
            var d = m.ToDynamic(kt);
            Assert.Equal("a", d["meta_tenant"]);
            Assert.Equal(new[] { 1f }, ((ReadOnlyMemory<float>)d["Placeholder"]!).ToArray());
            var back = ManifestRecord.FromDynamic(d, ["tenant"]);
            Assert.Equal(m.Key, back.Key);
            Assert.Equal(m.DocumentId, back.DocumentId);
            Assert.Equal(m.Partition, back.Partition);
            Assert.Equal(m.PartitionValues, back.PartitionValues);
            Assert.Equal(m.ContentHash, back.ContentHash);
            Assert.Equal(m.ChunkingHash, back.ChunkingHash);
            Assert.Equal(m.EmbeddingModelId, back.EmbeddingModelId);
            Assert.Equal(m.ChunkCount, back.ChunkCount);
            Assert.Equal(m.SourceName, back.SourceName);
            Assert.Equal(m.UpdatedAtUnixMs, back.UpdatedAtUnixMs);
            Assert.Equal(m.ContentStorage, back.ContentStorage);
        }
    }

    [Fact]
    public void Rejects_unsupported_distance_function()
    {
        var o = Options();
        o.DistanceFunction = DistanceFunction.EuclideanDistance;
        Assert.Throws<ArgumentException>(() => RecordDefinitions.Chunks(o));
    }
}
