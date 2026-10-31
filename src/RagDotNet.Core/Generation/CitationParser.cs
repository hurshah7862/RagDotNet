using System.Globalization;
using System.Text.RegularExpressions;

namespace RagDotNet.Generation;

internal static partial class CitationParser
{
    [GeneratedRegex(@"(?<![A-Za-z0-9_])\[(\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex Marker();

    public static IReadOnlyList<int> Parse(string text, int maxNumber)
    {
        var result = new List<int>();
        var lastAcceptedEnd = -1;
        foreach (Match m in Marker().Matches(text))
        {
            // A marker right after ']' counts only when that ']' closes an accepted marker ("[1][2]"), not "arr[x][1]".
            if (m.Index > 0 && text[m.Index - 1] == ']' && lastAcceptedEnd != m.Index) continue;
            lastAcceptedEnd = m.Index + m.Length;
            foreach (var part in m.Groups[1].Value.Split(','))
            {
                if (int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                    && n >= 1 && n <= maxNumber && !result.Contains(n))
                    result.Add(n);
            }
        }

        return result;
    }
}
