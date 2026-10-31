using Microsoft.ML.Tokenizers;

namespace RagDotNet.Chunking;

internal sealed class TokenCounter(Tokenizer tokenizer)
{
    public static Tokenizer CreateDefault() => TiktokenTokenizer.CreateForEncoding("cl100k_base");

    public int Count(string text) => text.Length == 0 ? 0 : tokenizer.CountTokens(text);

    /// <summary>Splits text into consecutive pieces of at most <paramref name="maxTokens"/> tokens each.</summary>
    public IEnumerable<string> SplitByTokens(string text, int maxTokens)
    {
        var rest = text;
        while (rest.Length > 0)
        {
            var index = tokenizer.GetIndexByTokenCount(rest, maxTokens, out _, out _, considerNormalization: false);
            if (index >= rest.Length)
            {
                yield return rest;
                yield break;
            }

            // Always make progress and never cut a surrogate pair.
            if (index <= 0) index = 1;
            if (index > 1 && char.IsLowSurrogate(rest[index]) && char.IsHighSurrogate(rest[index - 1])) index--;
            yield return rest[..index];
            rest = rest[index..];
        }
    }
}
