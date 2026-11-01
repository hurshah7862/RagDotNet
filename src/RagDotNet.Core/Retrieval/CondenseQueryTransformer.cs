using System.Text;
using Microsoft.Extensions.AI;

namespace RagDotNet.Retrieval;

/// <summary>Rewrites a follow-up question as a standalone question, using the conversation history.</summary>
internal sealed class CondenseQueryTransformer : IQueryTransformer
{
    public async Task<IReadOnlyList<string>> TransformAsync(QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chat = context.ChatClient
            ?? throw new InvalidOperationException($"{GetType().Name} needs a chat client. Call UseChatClient().");

        var conversation = new StringBuilder("Conversation:\n");
        foreach (var message in context.History)
        {
            conversation.Append(message.Role == ChatRole.Assistant ? "Assistant: " : "User: ").Append(message.Text).Append('\n');
        }

        conversation.Append("\nLast question: ").Append(context.Question);

        var response = await chat.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "Rewrite the user's last question as a standalone question that can be understood without the conversation. Keep the user's language. Reply with the question only."),
            new ChatMessage(ChatRole.User, conversation.ToString()),
        ], null, cancellationToken).ConfigureAwait(false);

        var text = Unquote((response.Text ?? "").Trim());
        return [text.Length > 0 ? text : context.Question];
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'') || (s[0] == '“' && s[^1] == '”')))
            return s[1..^1].Trim();
        return s;
    }
}
