using Microsoft.Extensions.AI;
using RagDotNet.Retrieval;

namespace RagDotNet.Generation;

/// <summary>Input to <see cref="IRagPromptBuilder.Build"/>.</summary>
/// <param name="Question">The user's question.</param>
/// <param name="ContextChunks">The chunks to put in the prompt; number them 1..n in list order.</param>
public sealed record RagPromptContext(string Question, IReadOnlyList<RetrievedChunk> ContextChunks)
{
    /// <summary>The earlier user and assistant messages of the conversation, oldest first, already limited to the history window. Empty for a single question.</summary>
    public IReadOnlyList<ChatMessage> History { get; init; } = [];
}
