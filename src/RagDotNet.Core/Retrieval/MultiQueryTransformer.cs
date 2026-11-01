using Microsoft.Extensions.AI;

namespace RagDotNet.Retrieval;

/// <summary>Asks the chat model for alternative phrasings of the first query and searches all of them.</summary>
public sealed class MultiQueryTransformer : IQueryTransformer
{
    private readonly int _count;

    /// <summary>Creates the transformer.</summary>
    /// <param name="count">How many alternatives to ask for, from 1 to 10.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not between 1 and 10.</exception>
    public MultiQueryTransformer(int count = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 10);
        _count = count;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No chat client is configured.</exception>
    public async Task<IReadOnlyList<string>> TransformAsync(QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chat = context.ChatClient
            ?? throw new InvalidOperationException($"{GetType().Name} needs a chat client. Call UseChatClient().");
        if (context.Queries.Count == 0) return context.Queries;

        var first = context.Queries[0];
        var response = await chat.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, $"Write {_count} different ways to ask the question below, for searching documents. Keep the meaning and the user's language. Put each on its own line, with no numbering or extra text."),
            new ChatMessage(ChatRole.User, first),
        ], null, cancellationToken).ConfigureAwait(false);

        var result = new List<string>(context.Queries);
        var alternatives = new List<string>();
        foreach (var raw in (response.Text ?? "").Split('\n'))
        {
            var line = Clean(raw);
            if (line.Length == 0) continue;
            if (result.Any(q => string.Equals(q.Trim(), line, StringComparison.OrdinalIgnoreCase))) continue;
            if (alternatives.Any(q => string.Equals(q, line, StringComparison.OrdinalIgnoreCase))) continue;
            alternatives.Add(line);
            if (alternatives.Count == _count) break;
        }

        result.InsertRange(1, alternatives);
        return result;
    }

    private static string Clean(string line)
    {
        var s = line.Trim();
        if (s.Length > 0 && s[0] is '-' or '*' or '•')
        {
            s = s[1..].Trim();
        }
        else
        {
            var i = 0;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            if (i > 0 && i < s.Length && s[i] is '.' or ')') s = s[(i + 1)..].Trim();
        }

        if (s.Length >= 2 && IsQuotePair(s[0], s[^1])) s = s[1..^1].Trim();
        return s;
    }

    private static bool IsQuotePair(char open, char close) =>
        (open == '"' && close == '"') || (open == '\'' && close == '\'') || (open == '“' && close == '”');
}
