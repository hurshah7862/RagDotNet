using Microsoft.Extensions.AI;
using RagDotNet.Evaluation.Datasets;
using RagDotNet.Evaluation.Providers;
using RagDotNet.Evaluation.Running;
using static RagDotNet.Evaluation.Tests.TestData;

namespace RagDotNet.Evaluation.Tests;

public class RunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Matrix_is_in_nested_order()
    {
        var m = EvaluationConfig.Matrix(["structure-aware"], [128, 256], [0, 0.1], ["vector"]);
        Assert.Equal(
            [
                new EvaluationConfig("structure-aware", 128, 0, "vector"),
                new EvaluationConfig("structure-aware", 128, 0.1, "vector"),
                new EvaluationConfig("structure-aware", 256, 0, "vector"),
                new EvaluationConfig("structure-aware", 256, 0.1, "vector"),
            ],
            m);
    }

    [Fact]
    public void Matrix_puts_mode_innermost()
    {
        var m = EvaluationConfig.Matrix(["a", "b"], [128], [0], ["vector", "hyde"]);
        Assert.Equal(["a/vector", "a/hyde", "b/vector", "b/hyde"], m.Select(c => $"{c.Chunker}/{c.Mode}"));
    }

    [Fact]
    public async Task Fake_embedder_is_deterministic_normalized_and_keeps_empty_text_zero()
    {
        using var e = new FakeEmbeddingGenerator(256);
        var v = await e.GenerateAsync(["Quill syncs notes, quill SYNCS!", "Quill syncs notes, quill SYNCS!", "  ...  "], cancellationToken: Ct);

        Assert.Equal(256, v[0].Vector.Length);
        Assert.Equal(v[0].Vector.ToArray(), v[1].Vector.ToArray());
        Assert.Equal(1.0, Math.Sqrt(v[0].Vector.ToArray().Sum(x => (double)x * x)), 5);
        Assert.All(v[2].Vector.ToArray(), x => Assert.Equal(0f, x));
        Assert.Equal("fake-embed", e.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId);
    }

    [Fact]
    public async Task Runner_scores_a_small_dataset_with_fake_embeddings()
    {
        var dataset = await DatasetLoader.LoadAsync(SmallDataset(), Ct);
        var configs = EvaluationConfig.Matrix(["structure-aware"], [128, 256], [0], ["vector"]);
        using var embedder = new FakeEmbeddingGenerator(256);

        var results = await new EvaluationRunner(embedder, 256, chat: null).RunAsync(dataset, configs, Ct);

        Assert.Equal(configs, results.Select(r => r.Config));
        Assert.All(results, r =>
        {
            Assert.True(r.ChunkCount > 0);
            Assert.Equal(3, r.Ranks.Count);
            Assert.All(r.Ranks, rank => Assert.True(rank is null or >= 1 and <= 10));
            Assert.InRange(r.Scores.Mrr, 0, 1);
            Assert.InRange(r.Scores.RecallAt5, 0, 1);
        });
        Assert.Contains(results.SelectMany(r => r.Ranks), r => r == 1);
    }

    [Fact]
    public async Task Chat_modes_without_a_chat_client_throw_before_ingesting()
    {
        var dataset = await DatasetLoader.LoadAsync(SmallDataset(), Ct);
        using var embedder = new FakeEmbeddingGenerator(256);
        var runner = new EvaluationRunner(embedder, 256, chat: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(dataset, EvaluationConfig.Matrix(["structure-aware"], [128], [0], ["hyde"]), Ct));
    }

    [Fact]
    public async Task Echo_client_replies_with_the_last_user_message()
    {
        using var chat = new EchoChatClient();
        var reply = await chat.GetResponseAsync([new(ChatRole.System, "sys"), new(ChatRole.User, "hello there")], cancellationToken: Ct);
        Assert.Equal("hello there", reply.Text);
    }
}
