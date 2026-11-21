using System.Text;

namespace RagDotNet.Keyword;

/// <summary>
/// Splits text into keyword terms: runs of Unicode letters and digits, lowercased invariantly. Terms joined by '.', '-'
/// or '_' inside a word (v2.3.1, ERR-404, max_tokens) are returned whole and then by part. English stop words are dropped;
/// there is no stemming.
/// </summary>
internal static class KeywordTokenizer
{
    /// <summary>Lucene's default English stop words (EnglishAnalyzer / StandardAnalyzer).</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "if", "in", "into", "is", "it", "no", "not", "of",
        "on", "or", "such", "that", "the", "their", "then", "there", "these", "they", "this", "to", "was", "will", "with",
    };

    /// <summary>The terms of <paramref name="text"/> in order, with repeats.</summary>
    public static IReadOnlyList<string> Terms(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var terms = new List<string>();
        var word = new StringBuilder();
        var parts = new List<string>();
        var part = new StringBuilder();

        void EndWord()
        {
            if (part.Length > 0) parts.Add(part.ToString());
            part.Clear();
            if (parts.Count > 1) Add(word.ToString());
            foreach (var p in parts) Add(p);
            parts.Clear();
            word.Clear();
        }

        void Add(string term)
        {
            if (!StopWords.Contains(term)) terms.Add(term);
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetterOrDigit(c))
            {
                var lower = char.ToLowerInvariant(c);
                word.Append(lower);
                part.Append(lower);
            }
            else if (c is '.' or '-' or '_' && part.Length > 0 && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))
            {
                // A joiner between two letters or digits keeps the word together.
                word.Append(c);
                parts.Add(part.ToString());
                part.Clear();
            }
            else if (word.Length > 0)
            {
                EndWord();
            }
        }

        if (word.Length > 0) EndWord();
        return terms;
    }
}
