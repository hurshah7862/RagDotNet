using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RagDotNet.Evaluation.Providers;

/// <summary>Deterministic chat client for fake mode: replies with the text of the last user message.</summary>
public sealed class EchoChatClient : IChatClient
{
    /// <summary>The model name shown in the report.</summary>
    public const string ModelId = "fake-echo";

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Echo(messages))));

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, Echo(messages));
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static string Echo(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
    }
}
