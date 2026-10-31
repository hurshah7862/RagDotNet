namespace RagDotNet.Chunking;

internal static class EmbeddingText
{
    public const string FormatVersion = "v1";

    public static string For(IReadOnlyList<string> headingPath, string text) =>
        headingPath.Count == 0 ? text : $"{string.Join(" > ", headingPath)}\n\n{text}";
}
