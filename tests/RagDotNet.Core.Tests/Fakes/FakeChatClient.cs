using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RagDotNet.Core.Tests.Fakes;

/// <summary>Chat client that returns scripted replies and records every request.</summary>
public sealed class FakeChatClient : IChatClient
{
    public Queue<string> Replies { get; } = new();

    public List<IList<ChatMessage>> Requests { get; } = [];

    public UsageDetails? Usage { get; set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var reply = Next(messages);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { Usage = Usage };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reply = Next(messages);
        await Task.CompletedTask;

        var start = 0;
        while (start < reply.Length)
        {
            var space = reply.IndexOf(' ', start);
            var end = space < 0 ? reply.Length : space + 1;
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply[start..end]);
            start = end;
        }

        if (Usage is not null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(Usage)]);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private string Next(IEnumerable<ChatMessage> messages)
    {
        Requests.Add(messages.ToList());
        return Replies.Count > 0 ? Replies.Dequeue() : throw new InvalidOperationException("No scripted reply");
    }
}
