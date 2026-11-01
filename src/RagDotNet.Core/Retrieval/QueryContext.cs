using Microsoft.Extensions.AI;

namespace RagDotNet.Retrieval;

/// <summary>The input handed to an <see cref="IQueryTransformer"/>.</summary>
/// <param name="Question">The user's original question.</param>
/// <param name="History">The conversation before the question; empty for a single question.</param>
/// <param name="Queries">The output of the previous step. Transformers chain by transforming these.</param>
/// <param name="ChatClient">The configured chat client, or null when none is configured.</param>
public sealed record QueryContext(
    string Question,
    IReadOnlyList<ChatMessage> History,
    IReadOnlyList<string> Queries,
    IChatClient? ChatClient);
