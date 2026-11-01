using System.Diagnostics;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class QueryTransformerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Fixed(params string[] queries) : IQueryTransformer
    {
        public Task<IReadOnlyList<string>> TransformAsync(QueryContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(queries);
    }

    private static async Task<(IRagPipeline Pipeline, FakeChatClient Chat)> Seeded(params IQueryTransformer[] transformers)
    {
        var (pipeline, _, _, chat, _) = Create(o => { foreach (var t in transformers) o.QueryTransformers.Add(t); });
        await pipeline.IngestAsync(Md("d1", "alpha beta"), cancellationToken: Ct);
        await pipeline.IngestAsync(Md("d2", "zulu yankee xray"), cancellationToken: Ct);
        return (pipeline, chat);
    }

    private static QueryPlanner PlannerOf(IRagPipeline p) => ((RagPipeline)p).Engine.QueryPlanner;

    [Fact]
    public async Task MultiQuery_adds_alternatives_and_searches_all_of_them()
    {
        var (pipeline, chat) = await Seeded(new MultiQueryTransformer(2));
        chat.Replies.Enqueue("zulu yankee xray\nsomething else");

        var results = await pipeline.RetrieveAsync("alpha beta", new RetrievalOptions { TopK = 2 }, Ct);

        Assert.Equal(["d1", "d2"], results.Select(r => r.DocumentId).Order());
        Assert.All(results, r => Assert.Equal(ScoreKind.Fused, r.ScoreKind));
        Assert.Contains("Write 2 different ways", chat.Requests[0][0].Text);
    }

    [Fact]
    public async Task MultiQuery_parses_messy_model_output()
    {
        var (pipeline, chat) = await Seeded(new MultiQueryTransformer(3));
        chat.Replies.Enqueue("1. alpha one\n\n- \"alpha two\"\n* alpha one\n2) ALPHA BETA\nalpha three\nalpha four");

        var queries = await PlannerOf(pipeline).PlanAsync("alpha beta", [], new RetrievalOptions(), chat, Ct);

        Assert.Equal(["alpha beta", "alpha one", "alpha two", "alpha three"], queries);
    }

    [Fact]
    public async Task HyDE_searches_with_the_generated_passage_instead_of_the_question()
    {
        var (pipeline, chat) = await Seeded(new HydeQueryTransformer());
        chat.Replies.Enqueue("zulu yankee xray");

        var results = await pipeline.RetrieveAsync("what is it?", new RetrievalOptions { TopK = 2 }, Ct);

        Assert.Equal("d2", results[0].DocumentId);
    }

    [Fact]
    public async Task Transformers_chain_in_order()
    {
        var (pipeline, chat) = await Seeded(new MultiQueryTransformer(1), new HydeQueryTransformer());
        chat.Replies.Enqueue("other way");
        chat.Replies.Enqueue("passage one");
        chat.Replies.Enqueue("passage two");

        var queries = await PlannerOf(pipeline).PlanAsync(
            "alpha beta", [], new RetrievalOptions(), chat, Ct);

        Assert.Equal(["passage one", "passage two"], queries);
        Assert.Equal(3, chat.Requests.Count);
        Assert.Contains("Write 1 different ways", chat.Requests[0][0].Text);
        Assert.Equal("alpha beta", chat.Requests[1][1].Text);
        Assert.Equal("other way", chat.Requests[2][1].Text);
    }

    [Fact]
    public async Task Per_call_override_replaces_configured_transformers()
    {
        var (pipeline, chat) = await Seeded(new MultiQueryTransformer());

        var results = await pipeline.RetrieveAsync("alpha beta", new RetrievalOptions { QueryTransformers = [] }, Ct);

        Assert.NotEmpty(results);
        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Empty_transformer_output_keeps_the_previous_queries()
    {
        var (pipeline, _) = await Seeded(new Fixed("  ", ""));

        var queries = await PlannerOf(pipeline).PlanAsync("alpha beta", [], new RetrievalOptions(), null, Ct);
        var results = await pipeline.RetrieveAsync("alpha beta", cancellationToken: Ct);

        Assert.Equal(["alpha beta"], queries);
        Assert.Equal("d1", results[0].DocumentId);
    }

    [Fact]
    public async Task Query_list_is_capped_at_10()
    {
        var (pipeline, _) = await Seeded(new Fixed(Enumerable.Range(1, 15).Select(i => $"query {i}").ToArray()));

        var queries = await PlannerOf(pipeline).PlanAsync("alpha beta", [], new RetrievalOptions(), null, Ct);

        Assert.Equal(10, queries.Count);
        Assert.Equal("query 1", queries[0]);
    }

    [Fact]
    public async Task Built_in_transformer_without_chat_client_throws()
    {
        var pipeline = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(new InMemoryVectorStore(), "test", 64)
            .Configure(o => o.QueryTransformers.Add(new MultiQueryTransformer()))
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RetrieveAsync("alpha", cancellationToken: Ct));

        Assert.Contains("needs a chat client", ex.Message);
    }

    [Fact]
    public void MultiQuery_count_out_of_range_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiQueryTransformer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiQueryTransformer(11));
        _ = new MultiQueryTransformer(1);
        _ = new MultiQueryTransformer(10);
    }

    [Fact]
    public async Task Ask_prompt_uses_the_original_question()
    {
        var (pipeline, chat) = await Seeded(new MultiQueryTransformer(1));
        chat.Replies.Enqueue("totally different words");
        chat.Replies.Enqueue("The answer [1].");

        await pipeline.AskAsync("alpha beta", cancellationToken: Ct);

        Assert.Equal(2, chat.Requests.Count);
        var last = chat.Requests[1].Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("Question: alpha beta", last);
        Assert.DoesNotContain("totally different words", string.Join("\n", chat.Requests[1].Select(m => m.Text)));
    }

    [Fact]
    public async Task Transform_span_is_emitted_without_query_text_by_default()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "RagDotNet" or "RagDotNet.Tests.Transform",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        using var rootActivity = new ActivitySource("RagDotNet.Tests.Transform").StartActivity("root");
        var (pipeline, chat) = await Seeded(new MultiQueryTransformer(1));
        chat.Replies.Enqueue("zulu yankee xray");

        await pipeline.RetrieveAsync("alpha beta", cancellationToken: Ct);

        Activity span;
        lock (spans) span = Assert.Single(spans, a => a.OperationName == "rag.transform" && a.TraceId == rootActivity!.TraceId);
        Assert.Equal("MultiQueryTransformer", span.GetTagItem("rag.transformer"));
        Assert.Equal(2, span.GetTagItem("rag.query.count"));
        Assert.Null(span.GetTagItem("rag.queries"));
    }
}
