using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using RagDotNet.Documents;

namespace RagDotNet.Readers;

/// <summary>Reads Markdown into headings, paragraphs, lists, tables and code blocks.</summary>
public sealed class MarkdownReader : IDocumentReader
{
    private static readonly string[] Extensions = [".md", ".markdown"];

    /// <inheritdoc />
    public bool CanRead(string? contentType, string fileName) =>
        contentType is not null
            ? PlainTextReader.ContentTypeMatches(contentType, "text/markdown")
            : Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var text = TextDecoding.Decode(stream);
        var pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
        var document = Markdown.Parse(text, pipeline);

        var blocks = new List<DocumentBlock>();
        var path = new List<(int Level, string Title)>();
        string? title = null;

        foreach (var block in document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block is ThematicBreakBlock)
            {
                continue;
            }

            var source = text.Substring(block.Span.Start, block.Span.Length).Trim();
            if (source.Length == 0)
            {
                continue;
            }

            if (block is HeadingBlock heading)
            {
                var headingText = InlineText(heading.Inline).Trim();
                path.RemoveAll(h => h.Level >= heading.Level);
                path.Add((heading.Level, headingText));
                if (heading.Level == 1 && title is null && headingText.Length > 0)
                {
                    title = headingText;
                }

                blocks.Add(new DocumentBlock(BlockKind.Heading, source, Snapshot(path)));
                continue;
            }

            var kind = block switch
            {
                CodeBlock => BlockKind.Code,
                Table => BlockKind.Table,
                ListBlock => BlockKind.List,
                _ => BlockKind.Paragraph,
            };
            blocks.Add(new DocumentBlock(kind, source, Snapshot(path)));
        }

        return Task.FromResult(new ParsedDocument(blocks, title));
    }

    private static string[] Snapshot(List<(int Level, string Title)> path) => path.Select(h => h.Title).ToArray();

    private static string InlineText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var inline in container.Descendants())
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
            }
        }

        return sb.ToString();
    }
}
