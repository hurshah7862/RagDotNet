using System.Text.RegularExpressions;
using RagDotNet.Documents;

namespace RagDotNet.Readers;

/// <summary>Reads plain text files, splitting paragraphs on blank lines.</summary>
public sealed partial class PlainTextReader : IDocumentReader
{
    private static readonly string[] Extensions = [".txt", ".text", ".log"];

    /// <inheritdoc />
    public bool CanRead(string? contentType, string fileName) =>
        contentType is not null
            ? ContentTypeMatches(contentType, "text/plain")
            : Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var text = TextDecoding.Decode(stream).Replace("\r\n", "\n").Replace('\r', '\n');
        IReadOnlyList<string> noPath = [];
        var blocks = BlankLines().Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => new DocumentBlock(BlockKind.Paragraph, p, noPath))
            .ToList();
        return Task.FromResult(new ParsedDocument(blocks));
    }

    internal static bool ContentTypeMatches(string contentType, string expected) =>
        contentType.Split(';')[0].Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\n[ \t]*(?:\n[ \t]*)+")]
    private static partial Regex BlankLines();
}
