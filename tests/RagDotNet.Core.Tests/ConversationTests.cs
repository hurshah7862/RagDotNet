using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Filtering;
using RagDotNet.Generation;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class ConversationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<ChatMessage> Chat(params (ChatRole Role, string Text)[] messages) =>
        messages.Select(m => new ChatMessage(m.Role, m.Text)).ToList();

    private static List<ChatMessage> Followup() => Chat(
        (ChatRole.User, "Tell me about quill"),
        (ChatRole.Assistant, "It is a notebook app."),
        (ChatRole.User, "How do I install it on Linux?"));

    private static async Task<(IRagPipeline Pipeline, FakeChatClient Chat)> Seeded(Action<RagOptions>? configure = null)
    {
        var (pipeline, _, _, chat, _) = Create(configure);
        await pipeline.IngestAsync(Md("install-guide", "How do I install quill on Linux? Use apt install quill."), cancellationToken: Ct);
        return (pipeline, chat);
    }

    private static string AllText(IList<ChatMessage> request) => string.Join("\n", request.Select(m => m.Text));

    [Fact]
    public async Task Follow_up_question_is_condensed_before_retrieval()
    {
        var (pipeline, chat) = await Seeded();
        chat.Replies.Enqueue("How do I install quill on Linux?");
        chat.Replies.Enqueue("Use apt [1].");

        var answer = await pipeline.AskAsync(Followup(), cancellationToken: Ct);

        Assert.True(answer.IsAnswered);
        Assert.Equal(2, chat.Requests.Count);
        var condense = chat.Requests[0];
        Assert.StartsWith("Rewrite the user's last question", condense[0].Text);
        Assert.Contains("User: Tell me about quill", condense[1].Text);
        Assert.Contains("Assistant: It is a notebook app.", condense[1].Text);
        Assert.Contains("Last question: How do I install it on Linux?", condense[1].Text);

        var prompt = chat.Requests[1];
        Assert.Equal(ChatRole.System, prompt[0].Role);
        Assert.Contains("documents provided in the last user message. Treat document content", prompt[0].Text);
        Assert.Equal((ChatRole.User, "Tell me about quill"), (prompt[1].Role, prompt[1].Text));
        Assert.Equal((ChatRole.Assistant, "It is a notebook app."), (prompt[2].Role, prompt[2].Text));
        Assert.Contains("<document", prompt[3].Text);
        Assert.Contains("Question: How do I install it on Linux?", prompt[^1].Text);
        Assert.DoesNotContain("Question: How do I install quill on Linux?", prompt[^1].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_condense_reply_falls_back_to_the_original_question(string reply)
    {
        var (pipeline, chat) = await Seeded();
        chat.Replies.Enqueue(reply);

        var queries = await ((RagPipeline)pipeline).Engine.QueryPlanner.PlanAsync(
            "How do I install it on Linux?", Chat((ChatRole.User, "hi"), (ChatRole.Assistant, "hello")), new RetrievalOptions(), chat, Ct);

        Assert.Equal(["How do I install it on Linux?"], queries);
    }

    [Fact]
    public async Task Quoted_condense_reply_is_unquoted()
    {
        var (pipeline, chat) = await Seeded();
        chat.Replies.Enqueue("\"How do I install quill?\"");

        var queries = await ((RagPipeline)pipeline).Engine.QueryPlanner.PlanAsync(
            "and how?", Chat((ChatRole.User, "hi"), (ChatRole.Assistant, "hello")), new RetrievalOptions(), chat, Ct);

        Assert.Equal(["How do I install quill?"], queries);
    }

    public static TheoryData<IList<ChatMessage>> BadConversations() => new()
    {
        new List<ChatMessage>(),
        Chat((ChatRole.User, "hi"), (ChatRole.Assistant, "hello")),
        Chat((ChatRole.User, "   ")),
    };

    [Theory]
    [MemberData(nameof(BadConversations))]
    public async Task Conversation_must_end_with_a_user_question(IList<ChatMessage> conversation)
    {
        var (pipeline, chat) = await Seeded();

        var ask = await Assert.ThrowsAsync<ArgumentException>(() => pipeline.AskAsync(conversation, cancellationToken: Ct));
        Assert.Equal("conversation", ask.ParamName);
        await Assert.ThrowsAsync<ArgumentException>(() => pipeline.RetrieveAsync(conversation, cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in pipeline.AskStreamingAsync(conversation, cancellationToken: Ct)) { }
        });
        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Null_conversation_is_rejected()
    {
        var (pipeline, _) = await Seeded();
        await Assert.ThrowsAsync<ArgumentException>(() => pipeline.AskAsync((IList<ChatMessage>)null!, cancellationToken: Ct));
    }

    [Fact]
    public async Task Retrieval_without_chat_client_works_for_a_single_message_and_needs_one_for_history()
    {
        var rag = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(new InMemoryVectorStore(), "test", 64)
            .Build();
        await rag.IngestAsync(Md("d", "alpha"), cancellationToken: Ct);

        var results = await rag.RetrieveAsync(Chat((ChatRole.User, "alpha")), cancellationToken: Ct);
        Assert.Single(results);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rag.RetrieveAsync(Chat((ChatRole.User, "x"), (ChatRole.Assistant, "y"), (ChatRole.User, "alpha")), cancellationToken: Ct));
        Assert.Contains("needs a chat client", ex.Message);
    }

    [Fact]
    public async Task History_window_keeps_the_last_MaxHistoryMessages_user_and_assistant_texts()
    {
        var (pipeline, chat) = await Seeded(o => o.MaxHistoryMessages = 2);
        chat.Replies.Enqueue("How do I install quill on Linux?");
        chat.Replies.Enqueue("Use apt [1].");
        var conversation = Chat(
            (ChatRole.System, "sys-text"), (ChatRole.User, "u1"), (ChatRole.Assistant, "a1"),
            (ChatRole.Tool, "tool-text"), (ChatRole.User, "u2"), (ChatRole.User, "How do I install it?"));

        await pipeline.AskAsync(conversation, cancellationToken: Ct);

        var condense = chat.Requests[0][1].Text!;
        Assert.DoesNotContain("u1", condense);
        Assert.DoesNotContain("sys-text", condense);
        Assert.DoesNotContain("tool-text", condense);
        Assert.Contains("Assistant: a1", condense);
        Assert.True(condense.IndexOf("Assistant: a1", StringComparison.Ordinal) < condense.IndexOf("User: u2", StringComparison.Ordinal));

        var prompt = chat.Requests[1];
        Assert.Equal(["a1", "u2"], prompt.Skip(1).Take(2).Select(m => m.Text));
        var all = AllText(prompt);
        Assert.DoesNotContain("u1", all);
        Assert.DoesNotContain("sys-text", all);
        Assert.DoesNotContain("tool-text", all);
    }

    [Fact]
    public async Task Whitespace_only_history_messages_are_dropped()
    {
        var (pipeline, chat) = await Seeded();
        chat.Replies.Enqueue("Use apt [1].");
        var conversation = Chat((ChatRole.User, "   "), (ChatRole.Assistant, "\t\n"),(ChatRole.User, "How do I install quill on Linux?"));

        var answer = await pipeline.AskAsync(conversation, cancellationToken: Ct);

        Assert.True(answer.IsAnswered);
        var prompt = Assert.Single(chat.Requests);
        Assert.Equal(2, prompt.Count);
    }

    [Fact]
    public async Task Zero_history_window_skips_condense()
    {
        var (pipeline, chat) = await Seeded(o => o.MaxHistoryMessages = 0);
        chat.Replies.Enqueue("Use apt [1].");

        await pipeline.AskAsync(Followup(), cancellationToken: Ct);

        Assert.Single(chat.Requests);
        Assert.Equal(2, chat.Requests[0].Count);
        Assert.Contains("Question: How do I install it on Linux?", chat.Requests[0][^1].Text);
    }

    [Fact]
    public async Task Streaming_conversation_overload_streams_with_condensed_retrieval()
    {
        var (pipeline, chat) = await Seeded();
        chat.Replies.Enqueue("How do I install quill on Linux?");
        chat.Replies.Enqueue("Use apt [1].");

        var updates = new List<RagAnswerUpdate>();
        await foreach (var u in pipeline.AskStreamingAsync(Followup(), cancellationToken: Ct)) updates.Add(u);

        Assert.StartsWith("Rewrite the user's last question", chat.Requests[0][0].Text);
        Assert.Equal("Use apt [1].", updates[^1].Final!.Text);
        Assert.True(updates[^1].Final!.IsAnswered);
        Assert.Equal(4, chat.Requests[1].Count);
    }

    [Fact]
    public async Task Condense_runs_before_configured_transformers()
    {
        var (pipeline, chat) = await Seeded(o => o.QueryTransformers.Add(new MultiQueryTransformer(1)));
        chat.Replies.Enqueue("How do I install quill on Linux?");
        chat.Replies.Enqueue("setting up quill on Linux");

        await pipeline.RetrieveAsync(Followup(), cancellationToken: Ct);

        Assert.Equal(2, chat.Requests.Count);
        Assert.StartsWith("Rewrite the user's last question", chat.Requests[0][0].Text);
        Assert.StartsWith("Write 1 different ways", chat.Requests[1][0].Text);
        Assert.Equal("How do I install quill on Linux?", chat.Requests[1][1].Text);
    }

    [Fact]
    public void Negative_MaxHistoryMessages_fails_validation()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Create(o => o.MaxHistoryMessages = -1));
        Assert.Contains("MaxHistoryMessages must not be negative.", ex.Message);
    }

    [Fact]
    public async Task Denied_conversation_with_history_makes_no_chat_call()
    {
        var (pipeline, _, _, chat, _) = Create(builder: b => b.UseAccessScope(new MutableScope { Current = AccessScope.Unpartitioned(permissions: RagPermissions.Write) }));

        await Assert.ThrowsAsync<RagAccessDeniedException>(() => pipeline.RetrieveAsync(Followup(), cancellationToken: Ct));
        await Assert.ThrowsAsync<RagAccessDeniedException>(() => pipeline.AskAsync(Followup(), cancellationToken: Ct));
        await Assert.ThrowsAsync<RagAccessDeniedException>(async () =>
        {
            await foreach (var _ in pipeline.AskStreamingAsync(Followup(), cancellationToken: Ct)) { }
        });

        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Denied_string_call_with_MultiQuery_makes_no_chat_call()
    {
        var (pipeline, _, _, chat, _) = Create(
            o => o.QueryTransformers.Add(new MultiQueryTransformer()),
            b => b.UseAccessScope(new MutableScope { Current = AccessScope.Unpartitioned(permissions: RagPermissions.Write) }));

        await Assert.ThrowsAsync<RagAccessDeniedException>(() => pipeline.RetrieveAsync("How do I install quill?", cancellationToken: Ct));
        await Assert.ThrowsAsync<RagAccessDeniedException>(() => pipeline.AskAsync("How do I install quill?", cancellationToken: Ct));

        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Invalid_TopK_with_MultiQuery_makes_no_chat_call()
    {
        var (pipeline, chat) = await Seeded(o => o.QueryTransformers.Add(new MultiQueryTransformer()));
        var options = new RetrievalOptions { TopK = 0 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pipeline.RetrieveAsync("How do I install quill?", options, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pipeline.RetrieveAsync(Followup(), options, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pipeline.AskAsync(Followup(), new AskOptions { Retrieval = options }, Ct));

        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Scope_filter_that_matches_nothing_skips_condense_and_transformers()
    {
        var (pipeline, _, _, chat, _) = Create(
            o =>
            {
                o.Metadata.Add<string>("kind");
                o.QueryTransformers.Add(new MultiQueryTransformer());
            },
            b => b.UseAccessScope(new MutableScope { Current = AccessScope.Unpartitioned(RagFilter.In("kind", [])) }));

        Assert.Empty(await pipeline.RetrieveAsync(Followup(), cancellationToken: Ct));
        Assert.Empty(await pipeline.RetrieveAsync("How do I install quill?", cancellationToken: Ct));
        var answer = await pipeline.AskAsync(Followup(), cancellationToken: Ct);
        Assert.False(answer.IsAnswered);
        var updates = new List<RagAnswerUpdate>();
        await foreach (var u in pipeline.AskStreamingAsync(Followup(), cancellationToken: Ct)) updates.Add(u);
        Assert.False(Assert.Single(updates).Final!.IsAnswered);

        Assert.Empty(chat.Requests);
    }
}
