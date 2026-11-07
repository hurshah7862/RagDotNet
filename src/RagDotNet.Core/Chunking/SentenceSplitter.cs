using System.Text.RegularExpressions;

namespace RagDotNet.Chunking;

/// <summary>The sentence boundary rule shared by the built-in chunkers: whitespace after a sentence-ending mark.</summary>
internal static partial class SentenceSplitter
{
    [GeneratedRegex(@"(?<=[.!?。！？])\s+")]
    public static partial Regex Pattern();

    /// <summary>Splits text into its sentences, dropping blank pieces.</summary>
    public static IReadOnlyList<string> Split(string text) =>
        [.. Pattern().Split(text).Where(s => !string.IsNullOrWhiteSpace(s))];
}
