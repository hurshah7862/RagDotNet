using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Ingestion;
using RagDotNet.Keyword;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests.Hybrid;

public class KeywordIndexSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sections(int n, string prefix = "Body") =>
        string.Concat(Enumerable.Range(1, n).Select(i => $"# Section {i}\n\n{prefix} text number {i}\n\n"));

    private static List<string> Keys(RecordingKeywordIndex index) => index.Chunks.Keys.Order(StringComparer.Ordinal).ToList();

    private static int CallsStartingWith(RecordingKeywordIndex index, string prefix) => index.Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public async Task Ingest_upserts_chunks_before_the_manifest_commit()
    {
        var index = new RecordingKeywordIndex();
        var (rag, _, _, _, _) = Create(o => o.Metadata.Add<string>("team"), b => b.UseKeywordIndex(index));
        var engine = ((RagPipeline)rag).Engine;
        string? hashAtUpsert = null;
        var recordsAtUpsert = -1;
        index.BeforeUpsert = async chunks =>
        {
            hashAtUpsert = (await engine.Manifests.GetAsync("", "doc", Ct))!.ContentHash;
            recordsAtUpsert = (await engine.Chunks.GetAsync(chunks.Select(c => c.ChunkKey), false, Ct)).Count;
        };

        await rag.IngestAsync(Md("doc", Sections(3), new Dictionary<string, object?> { ["team"] = "core" }), cancellationToken: Ct);

        Assert.Equal("", hashAtUpsert);
        Assert.Equal(3, recordsAtUpsert);
        Assert.Equal(["doc:0", "doc:1", "doc:2"], Keys(index));
        var first = index.Chunks["doc:0"];
        Assert.Contains("Body text number 1", first.Text);
        Assert.Equal("doc", first.DocumentId);
        Assert.Equal("", first.Partition);
        Assert.Equal("core", first.Metadata["team"]);
    }

    [Fact]
    public async Task Unchanged_reingest_does_not_touch_the_index()
    {
        var index = new RecordingKeywordIndex();
        var (rag, _, _, _, _) = Create(builder: b => b.UseKeywordIndex(index));

        await rag.IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);
        var r = await rag.IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);

        Assert.Equal(IngestStatus.Unchanged, r.Status);
        Assert.Equal(1, CallsStartingWith(index, "upsert"));
        Assert.Equal(0, CallsStartingWith(index, "delete"));
    }

    [Fact]
    public async Task Shrinking_update_deletes_the_stale_keys()
    {
        var index = new RecordingKeywordIndex();
        var (rag, _, _, _, _) = Create(builder: b => b.UseKeywordIndex(index));

        await rag.IngestAsync(Md("doc", Sections(5)), cancellationToken: Ct);
        await rag.IngestAsync(Md("doc", Sections(2, "Changed")), cancellationToken: Ct);

        Assert.Equal(["doc:0", "doc:1"], Keys(index));
        Assert.Contains("delete doc:2,doc:3,doc:4", index.Calls);
        Assert.Contains("Changed", index.Chunks["doc:0"].Text);
    }

    [Fact]
    public async Task Delete_removes_the_document_keys_before_the_manifest()
    {
        var index = new RecordingKeywordIndex();
        var (rag, _, _, _, _) = Create(builder: b => b.UseKeywordIndex(index));
        var engine = ((RagPipeline)rag).Engine;
        await rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);
        await rag.IngestAsync(Md("other", Sections(1)), cancellationToken: Ct);
        var manifestAtDelete = false;
        index.BeforeDelete = async _ => manifestAtDelete = await engine.Manifests.GetAsync("", "doc", Ct) is not null;

        Assert.True(await rag.DeleteDocumentAsync("doc", Ct));

        Assert.True(manifestAtDelete);
        Assert.Equal(["other:0"], Keys(index));
    }

    [Fact]
    public async Task Admin_delete_removes_keys()
    {
        var index = new RecordingKeywordIndex();
        var (rag, builder, _, _, _) = Create(builder: b => b.UseKeywordIndex(index));
        await rag.IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);

        Assert.True(await builder.BuildAdmin().DeleteDocumentAsync("doc", null, Ct));

        Assert.Empty(Keys(index));
    }

    [Fact]
    public async Task Same_document_id_in_two_partitions_stays_apart()
    {
        var index = new RecordingKeywordIndex();
        var scope = new MutableScope { Current = TestScopes.Tenant("a") };
        var (rag, _, _, _, _) = Create(
            o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); },
            b => b.UseAccessScope(scope).UseKeywordIndex(index));
        await rag.IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);
        scope.Current = TestScopes.Tenant("b");
        await rag.IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);

        scope.Current = TestScopes.Tenant("a");
        Assert.True(await rag.DeleteDocumentAsync("doc", Ct));

        Assert.Equal(["tenant=b|doc:0", "tenant=b|doc:1"], Keys(index));
        Assert.All(index.Chunks.Values, c => Assert.Equal("tenant=b", c.Partition));
    }

    [Fact]
    public async Task Hybrid_Off_never_writes_the_index()
    {
        var index = new RecordingKeywordIndex();
        var (rag, _, _, _, _) = Create(o => o.Hybrid = HybridMode.Off, b => b.UseKeywordIndex(index));

        await rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);
        await rag.IngestAsync(Md("doc", Sections(1, "Changed")), cancellationToken: Ct);
        await rag.DeleteDocumentAsync("doc", Ct);

        Assert.Equal(0, CallsStartingWith(index, "upsert"));
        Assert.Equal(0, CallsStartingWith(index, "delete"));
    }

    [Fact]
    public async Task Failure_in_keyword_upsert_fails_ingest_and_retry_converges()
    {
        var index = new RecordingKeywordIndex { FailNextUpsert = true };
        var (rag, _, _, _, _) = Create(builder: b => b.UseKeywordIndex(index));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct));
        var r = await rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);

        Assert.Contains(r.Status, new[] { IngestStatus.Created, IngestStatus.Updated });
        Assert.Equal(["doc:0", "doc:1", "doc:2"], Keys(index));
    }

    [Fact]
    public void Default_builder_and_AddRag_register_InMemoryKeywordIndex()
    {
        var (rag, _, _, _, _) = Create();
        Assert.IsType<InMemoryKeywordIndex>(((RagPipeline)rag).Engine.KeywordIndex);
        Assert.Null(((RagPipeline)rag).Engine.Reranker);

        using var provider = Services(b => { }).BuildServiceProvider();
        Assert.IsType<InMemoryKeywordIndex>(provider.GetRequiredService<RagEngine>().KeywordIndex);
        Assert.Null(provider.GetRequiredService<RagEngine>().Reranker);
    }

    [Fact]
    public void AddKeywordIndex_and_AddReranker_replace_earlier_registrations()
    {
        using var provider = Services(b => b.AddKeywordIndex<RecordingKeywordIndex>().AddReranker<FirstReranker>().AddReranker<SecondReranker>())
            .BuildServiceProvider();
        var engine = provider.GetRequiredService<RagEngine>();

        Assert.IsType<RecordingKeywordIndex>(engine.KeywordIndex);
        Assert.IsType<SecondReranker>(engine.Reranker);
        Assert.Single(provider.GetServices<IKeywordIndex>());

        using var back = Services(b => b.AddKeywordIndex<RecordingKeywordIndex>().AddInMemoryKeywordIndex()).BuildServiceProvider();
        Assert.IsType<InMemoryKeywordIndex>(back.GetRequiredService<RagEngine>().KeywordIndex);
    }

    [Fact]
    public void Builder_UseKeywordIndex_and_UseReranker_set_the_engine()
    {
        var index = new RecordingKeywordIndex();
        var reranker = new FirstReranker();
        var (rag, _, _, _, _) = Create(builder: b => b.UseKeywordIndex(index).UseReranker(reranker));

        Assert.Same(index, ((RagPipeline)rag).Engine.KeywordIndex);
        Assert.Same(reranker, ((RagPipeline)rag).Engine.Reranker);
    }

    private static ServiceCollection Services(Action<IRagBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        configure(services.AddRag(o => o.EmbeddingDimensions = 64));
        return services;
    }

    private class FirstReranker : IReranker
    {
        public Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RerankResult>>([]);
    }

    private sealed class SecondReranker : FirstReranker;
}
