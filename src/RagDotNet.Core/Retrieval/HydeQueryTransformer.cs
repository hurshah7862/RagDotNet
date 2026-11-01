using Microsoft.Extensions.AI;

namespace RagDotNet.Retrieval;

/// <summary>Hypothetical document embeddings: replaces each query with a generated passage that would answer it, and searches with the passage.</summary>
public sealed class HydeQueryTransformer : IQueryTransformer
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No chat client is configured.</exception>
    public async Task<IReadOnlyList<string>> TransformAsync(QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chat = context.ChatClient
            ?? throw new InvalidOperationException($"{GetType().Name} needs a chat client. Call UseChatClient().");

        var result = new List<string>(context.Queries.Count);
        foreach (var query in context.Queries)
        {
            var response = await chat.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "Write a short passage (2 to 4 sentences) that would answer the question. It is used only to search documents, so it may contain guesses. Reply with the passage only."),
                new ChatMessage(ChatRole.User, query),
            ], null, cancellationToken).ConfigureAwait(false);
            var passage = response.Text?.Trim();
            result.Add(string.IsNullOrEmpty(passage) ? query : passage);
        }

        return result;
    }
}
