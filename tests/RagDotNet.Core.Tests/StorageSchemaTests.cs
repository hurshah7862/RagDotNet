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
    public void Chunk_definition_has_indexed_partition_field()
    {
        var p = RecordDefinitions.Chunks(Options()).Properties.OfType<VectorStoreDataProperty>().Single(x => x.Name == "Partition");
        Assert.Equal(typeof(string), p.Type);
        Assert.True(p.IsIndexed);
    }

    [Fact]
    public void Manifest_definition_declares_every_metadata_field()
    {
        var o = Options();
        o.Metadata.Add<string[]>("groups");
        var names = RecordDefinitions.Manifests(o).Properties.Select(x => x.Name).ToList();
        Assert.Contains("meta_tenant", names);
        Assert.Contains("meta_groups", names);
    }

    [Fact]
    public async Task Ingested_chunks_and_manifest_carry_partition_and_metadata()
    {
        var (pipeline, _, _, _, store) = Fakes.TestRag.Create(x =>
        {
            x.Metadata.Add<string>("tenant");
            x.Metadata.Add<string[]>("groups");
        });
        await pipeline.IngestAsync(Fakes.TestRag.Md("d1", "# T" + (char)10 + (char)10 + "alpha", new Dictionary<string, object?> { ["groups"] = new[] { "g1" } }), cancellationToken: TestContext.Current.CancellationToken);

        var options = new RagOptions { EmbeddingDimensions = 64 };
        options.Metadata.Add<string>("tenant");
        options.Metadata.Add<string[]>("groups");
        var chunks = store.GetDynamicCollection("test", RecordDefinitions.Chunks(options));
        var manifests = store.GetDynamicCollection("test_documents", RecordDefinitions.Manifests(options));
        var chunk = await chunks.GetAsync("d1:0", cancellationToken: TestContext.Current.CancellationToken);
        var manifest = await manifests.GetAsync("d1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(chunk);
        Assert.NotNull(manifest);
        Assert.Equal("", chunk["Partition"]);
        Assert.Equal(new[] { "g1" }, (string[])manifest["meta_groups"]!);
        Assert.Null(manifest["meta_tenant"]);
    }

    [Fact]
    public void Common_area_manifest_round_trips_without_partition_values()
    {
        var o = Options();
        var m = new ManifestRecord("d1", "d1", "", new Dictionary<string, string>(), "h", "c", "m", 1, "d1.md", 0,
            ContentStorageMode.VectorStore, "mh", 1, new Dictionary<string, object?> { ["tenant"] = null });
        var back = ManifestRecord.FromDynamic(m.ToDynamic(o.KeyType), o);
        Assert.Empty(back.PartitionValues);
        Assert.Null(back.Metadata["tenant"]);
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
        Assert.Equal("a=x%7Cy&b=2|doc%3A1:0", ChunkKeys.Chunk(p1, "doc:1", 0));
        Assert.Equal("a=x%7Cy&b=2|doc%3A1", ChunkKeys.Document(p1, "doc:1"));
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

    public static TheoryData<string[], string[]> DistinctStringArrays() => new()
    {
        { [], [""] },
        { [""], ["", ""] },
        { ["a,b"], ["a", "b"] },
        { ["a"], ["a", ""] },
        { ["ab", "c"], ["a", "bc"] },
        { ["1:a"], ["a"] },
    };

    [Theory]
    [MemberData(nameof(DistinctStringArrays))]
    public void Metadata_hash_distinguishes_distinct_string_arrays(string[] a, string[] b)
    {
        static string Hash(string[] v) => Hashing.MetadataHash(new Dictionary<string, object?> { ["tags"] = v });
        Assert.NotEqual(Hash(a), Hash(b));
        Assert.Equal(Hash(a), Hash([.. a]));
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
        var m = new ManifestRecord("tenant=a|doc%3A1", "doc:1", "tenant=a", new Dictionary<string, string> { ["tenant"] = "a" },
            "ch", "kh", "model", 7, "file.md", 1234567890123L, ContentStorageMode.External, "", 0, new Dictionary<string, object?> { ["tenant"] = "a" });
        foreach (var kt in new[] { RagKeyType.String, RagKeyType.Guid })
        {
            var d = m.ToDynamic(kt);
            Assert.Equal("a", d["meta_tenant"]);
            Assert.Equal(new[] { 1f }, ((ReadOnlyMemory<float>)d["Placeholder"]!).ToArray());
            var back = ManifestRecord.FromDynamic(d, Options());
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
    public void Keys_are_injective_across_partitions()
    {
        Assert.NotEqual(ChunkKeys.Document("a=1", "x"), ChunkKeys.Document("", "a=1|x"));
        Assert.NotEqual(ChunkKeys.Chunk("a=1", "x", 0), ChunkKeys.Chunk("", "a=1|x", 0));
        Assert.NotEqual(ChunkKeys.Document("a=1", "d"), ChunkKeys.Document("a=2", "d"));
        var ids = new[] { "a|b", "a=b", "a:b", "a%7Cb", "a b" };
        Assert.Equal(ids.Length, ids.Select(i => ChunkKeys.Chunk("", i, 1)).Distinct().Count());
    }

    [Fact]
    public void Key_inputs_are_validated()
    {
        var lone = "x\ud800y";
        Assert.Throws<ArgumentException>(() => ChunkKeys.Partition(new Dictionary<string, string> { ["t"] = lone }));
        Assert.Throws<ArgumentException>(() => ChunkKeys.Partition(new Dictionary<string, string> { [lone] = "v" }));
        Assert.Throws<ArgumentException>(() => ChunkKeys.Partition(new Dictionary<string, string> { ["t"] = null! }));
        Assert.Throws<ArgumentException>(() => ChunkKeys.Partition(new Dictionary<string, string> { [""] = "v" }));
        Assert.Throws<ArgumentException>(() => ChunkKeys.Document("", lone));
        Assert.Throws<ArgumentException>(() => ChunkKeys.Document("", ""));
        Assert.Throws<ArgumentException>(() => ChunkKeys.Document("", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkKeys.Chunk("", "d", -1));
        Assert.Equal("t=%F0%9F%98%80", ChunkKeys.Partition(new Dictionary<string, string> { ["t"] = "😀" }));
    }

    [Fact]
    public void Manifest_rejects_missing_partition_value_and_mismatched_partition()
    {
        var m = new ManifestRecord("tenant=a|d", "d", "tenant=a", new Dictionary<string, string> { ["tenant"] = "a" },
            "c", "k", "m", 1, "s", 1, ContentStorageMode.VectorStore, "", 0, new Dictionary<string, object?> { ["tenant"] = "a" });
        var d = m.ToDynamic(RagKeyType.String);
        d.Remove("meta_tenant");
        Assert.Throws<InvalidOperationException>(() => ManifestRecord.FromDynamic(d, Options()));
        d["meta_tenant"] = 5;
        Assert.Throws<InvalidOperationException>(() => ManifestRecord.FromDynamic(d, Options()));
        d["meta_tenant"] = "b";
        Assert.Throws<InvalidOperationException>(() => ManifestRecord.FromDynamic(d, Options()));
        var bad = m with { Partition = "tenant=zzz" };
        Assert.Throws<InvalidOperationException>(() => bad.ToDynamic(RagKeyType.String));
    }

    [Fact]
    public void Cosine_distance_is_accepted()
    {
        var o = Options();
        o.DistanceFunction = DistanceFunction.CosineDistance;
        var emb = Assert.IsType<VectorStoreVectorProperty>(RecordDefinitions.Chunks(o).Properties.Single(p => p.Name == "Embedding"));
        Assert.Equal(DistanceFunction.CosineDistance, emb.DistanceFunction);
    }

    [Fact]
    public void Rejects_unsupported_distance_function()
    {
        var o = Options();
        o.DistanceFunction = DistanceFunction.EuclideanDistance;
        Assert.Throws<ArgumentException>(() => RecordDefinitions.Chunks(o));
    }
}
