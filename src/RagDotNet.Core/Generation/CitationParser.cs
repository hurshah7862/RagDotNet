using System.Globalization;
using System.Text.RegularExpressions;

namespace RagDotNet.Generation;

internal static partial class CitationParser
{
    [GeneratedRegex(@"(?<![A-Za-z0-9_\]])\[(\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex Marker();

    public static IReadOnlyList<int> Parse(string text, int maxNumber)
    {
        var result = new List<int>();
        foreach (Match m in Marker().Matches(text))
        {
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
