using Microsoft.Extensions.AI;
using RagDotNet.Core.Tests.Fakes;

namespace RagDotNet.Core.Tests;

public class FakesTests
{
    private static double Cosine(ReadOnlyMemory<float> x, ReadOnlyMemory<float> y)
    {
        var a = x.ToArray();
        var b = y.ToArray();
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot; // vectors are L2-normalised
    }

    [Fact]
    public async Task Embedder_is_deterministic_and_normalised()
    {
        var e = new FakeEmbeddingGenerator();
        var a = await e.GenerateVectorAsync("install on linux", cancellationToken: TestContext.Current.CancellationToken);
        var b = await e.GenerateVectorAsync("install on linux", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(a.ToArray(), b.ToArray());
        Assert.Equal(64, a.Length);
        Assert.Equal(1.0, Math.Sqrt(a.ToArray().Sum(x => (double)x * x)), 3);
        Assert.Equal(2, e.CallCount);
    }

    [Fact]
    public async Task Embedder_puts_overlapping_text_closer_than_unrelated_text()
    {
        var e = new FakeEmbeddingGenerator();
        var ct = TestContext.Current.CancellationToken;
        var q = await e.GenerateVectorAsync("install linux", cancellationToken: ct);
        var near = await e.GenerateVectorAsync("linux install guide", cancellationToken: ct);
        var far = await e.GenerateVectorAsync("banana bread recipe", cancellationToken: ct);
        Assert.True(Cosine(q, near) > Cosine(q, far));
    }

    [Fact]
    public async Task Embedder_gives_zero_vector_for_empty_text()
    {
        var e = new FakeEmbeddingGenerator();
        var v = await e.GenerateVectorAsync("  ", cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(v.ToArray(), x => Assert.Equal(0f, x));
    }

    [Fact]
    public void Embedder_reports_model_id()
    {
        var e = new FakeEmbeddingGenerator();
        Assert.Equal("fake-embed", e.GetService<EmbeddingGeneratorMetadata>()!.DefaultModelId);
    }

    [Fact]
    public async Task Chat_returns_scripted_replies_in_order_and_records_requests()
    {
        var c = new FakeChatClient();
        c.Replies.Enqueue("one");
        c.Replies.Enqueue("two");
        var ct = TestContext.Current.CancellationToken;
        var r1 = await c.GetResponseAsync([new ChatMessage(ChatRole.User, "q1")], cancellationToken: ct);
        var r2 = await c.GetResponseAsync([new ChatMessage(ChatRole.User, "q2")], cancellationToken: ct);
        Assert.Equal("one", r1.Text);
        Assert.Equal("two", r2.Text);
        Assert.Equal(2, c.Requests.Count);
        Assert.Equal("q2", c.Requests[1][0].Text);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.GetResponseAsync([new ChatMessage(ChatRole.User, "q3")], cancellationToken: ct));
        Assert.Equal("No scripted reply", ex.Message);
    }

    [Fact]
    public async Task Chat_streaming_concatenates_to_reply()
    {
        var c = new FakeChatClient { Usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 4 } };
        c.Replies.Enqueue("Hello world [1]");
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in c.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")], cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(u);
        Assert.Equal("Hello world [1]", string.Concat(updates.Select(u => u.Text)));
        Assert.Contains(updates[^1].Contents, x => x is UsageContent);
    }
}
