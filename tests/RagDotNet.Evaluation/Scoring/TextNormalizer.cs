using System.Text;

namespace RagDotNet.Evaluation.Scoring;

/// <summary>Normalizes text before evidence matching: drops <c>*</c>, <c>_</c> and backticks, collapses whitespace, trims and lower-cases.</summary>
public static class TextNormalizer
{
    /// <summary>Normalizes <paramref name="text"/>.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (c is '*' or '_' or '`')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
