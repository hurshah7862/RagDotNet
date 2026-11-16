using RagDotNet.Evaluation.Cli;
using RagDotNet.Evaluation.Datasets;
using RagDotNet.Evaluation.Providers;
using RagDotNet.Evaluation.Running;
using RagDotNet.Retrieval;

namespace RagDotNet.Evaluation.Tests;

public class HybridModeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BaseSet => Path.Combine(AppContext.BaseDirectory, "Dataset", "base");

    private static readonly string[] OneConfig = ["--dataset", BaseSet, "--chunkers", "structure-aware", "--max-tokens", "512", "--overlap", "0.1"];

    [Fact]
    public async Task Default_report_has_no_retrieval_column_or_exact_term_table()
    {
        var (code, report, err) = await CliTests.Run(OneConfig);

        Assert.True(code == 0, err);
        Assert.Contains("| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |\n", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Retrieval", report, StringComparison.Ordinal);
        Assert.DoesNotContain("exact-term", report, StringComparison.Ordinal);
        Assert.Contains("- Set `base`: 8 documents, 48 questions, hash ", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Retrieval_option_parses_and_defaults_to_vector()
    {
        Assert.Equal([EvaluationConfig.RetrievalVector], Options.Parse([]).Retrievals);
        Assert.Equal(["vector", "hybrid", "hybrid-rerank"], Options.Parse(["--retrieval", "vector,hybrid,hybrid-rerank"]).Retrievals);
        Assert.False(Options.Parse(["--retrieval", "hybrid-rerank"]).NeedsChat);
        Assert.True(Options.Parse(["--embedder", "ollama", "--retrieval", "hybrid-rerank"]).NeedsChat);
    }

    [Fact]
    public async Task Unknown_retrieval_mode_exits_1()
    {
        var (code, _, err) = await CliTests.Run("--retrieval", "bm25");

        Assert.Equal(1, code);
        Assert.Contains("Invalid value 'bm25' for --retrieval", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Matrix_puts_retrieval_innermost()
    {
        var m = EvaluationConfig.Matrix(["a"], [128], [0], ["vector", "hyde"], ["vector", "hybrid"]);

        Assert.Equal(["vector/vector", "vector/hybrid", "hyde/vector", "hyde/hybrid"], m.Select(c => $"{c.Mode}/{c.Retrieval}"));
        Assert.All(EvaluationConfig.Matrix(["a"], [128], [0], ["vector"]), c => Assert.Equal(EvaluationConfig.RetrievalVector, c.Retrieval));
    }

    [Fact]
    public async Task Vector_mode_is_unchanged_by_hybrid_configs_in_the_same_run()
    {
        var sets = await DatasetLoader.LoadSetsAsync(BaseSet, Ct);
        using var embedder = new FakeEmbeddingGenerator(256);
        var vectorOnly = EvaluationConfig.Matrix(["structure-aware"], [512], [0.1], ["vector"]);
        var both = EvaluationConfig.Matrix(["structure-aware"], [512], [0.1], ["vector"], ["vector", "hybrid"]);

        var alone = await new EvaluationRunner(embedder, 256, chat: null).RunAsync(sets, vectorOnly, Ct);
        var mixed = await new EvaluationRunner(embedder, 256, chat: null).RunAsync(sets, both, Ct);

        Assert.Equal(alone[0].Ranks, mixed[0].Ranks);
        // Hybrid search reads the same chunks but ranks them differently.
        Assert.NotEqual(mixed[0].Ranks, mixed[1].Ranks);
    }

    [Fact]
    public async Task Hybrid_rerank_needs_a_reranker()
    {
        var sets = await DatasetLoader.LoadSetsAsync(BaseSet, Ct);
        using var embedder = new FakeEmbeddingGenerator(256);
        var configs = EvaluationConfig.Matrix(["structure-aware"], [512], [0.1], ["vector"], ["hybrid-rerank"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new EvaluationRunner(embedder, 256, chat: null).RunAsync(sets, configs, Ct));
        var results = await new EvaluationRunner(embedder, 256, chat: null, reranker: new FakeReranker()).RunAsync(sets, configs, Ct);
        Assert.Equal(48, results[0].Ranks.Count);
    }

    [Fact]
    public void Hybrid_rerank_uses_FakeReranker_in_fake_mode_and_LlmReranker_with_ollama()
    {
        using var chat = new EchoChatClient();

        Assert.IsType<FakeReranker>(Program.CreateReranker(Options.Parse(["--retrieval", "hybrid-rerank"]), chat));
        Assert.IsType<LlmReranker>(Program.CreateReranker(Options.Parse(["--embedder", "ollama", "--retrieval", "hybrid-rerank"]), chat));
        Assert.Null(Program.CreateReranker(Options.Parse(["--retrieval", "vector,hybrid"]), chat));
    }

    [Fact]
    public async Task Fake_reranker_keeps_the_fused_order_with_rerank_scores()
    {
        var candidates = Enumerable.Range(0, 4)
            .Select(i => new RetrievedChunk($"d:{i}", "d", i, "t", 1.0 / (i + 1), ScoreKind.Fused, null, null, [], "", new Dictionary<string, object?>()))
            .ToList();

        var ranked = await new FakeReranker().RerankAsync("q", candidates, 3, Ct);

        Assert.Equal([0, 1, 2], ranked.Select(r => r.Index));
        Assert.All(ranked, r => Assert.InRange(r.Score, 0, 1));
        Assert.True(ranked[0].Score > ranked[1].Score);
    }

    [Fact]
    public async Task Exact_term_subset_is_reported_per_mode()
    {
        var (code, report, err) = await CliTests.Run([.. OneConfig, "--retrieval", "vector,hybrid,hybrid-rerank"]);

        Assert.True(code == 0, err);
        Assert.Contains("| Chunker | MaxTokens | Overlap | Mode | Retrieval | Chunks | R@1 | R@3 | R@5 | MRR |\n", report, StringComparison.Ordinal);
        Assert.Contains("### base · exact-term (8 questions)", report, StringComparison.Ordinal);
        foreach (var retrieval in new[] { "vector", "hybrid", "hybrid-rerank" })
            Assert.Equal(2, report.Split('\n').Count(l => l.StartsWith($"| structure-aware | 512 | 0.1 | vector | {retrieval} |", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Base_set_tags_eight_exact_term_questions()
    {
        var set = Assert.Single(await DatasetLoader.LoadSetsAsync(BaseSet, Ct));

        var exact = set.Dataset.Questions.Where(q => q.Tags?.Contains(Question.ExactTerm) == true).ToList();

        Assert.Equal(8, exact.Count);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"exact-{i}"), exact.Select(q => q.Id));
        Assert.Equal(40, set.Dataset.Questions.Count(q => q.Tags is null));
    }
}
