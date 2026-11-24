using Microsoft.Extensions.DataIngestion;
using RagDotNet.Documents;
using RagDotNet.Ocr;

namespace RagDotNet.DataIngestion;

/// <summary>
/// Uses a Microsoft.Extensions.DataIngestion <see cref="IngestionDocumentReader"/> as a RagDotNet <see cref="IDocumentReader"/>.
/// Headers become heading blocks (and the heading path), tables table blocks, images with bytes OCR candidates, and every
/// other element a paragraph.
/// </summary>
public sealed class DataIngestionReader : IDocumentReader
{
    private readonly IngestionDocumentReader _reader;
    private readonly HashSet<string> _extensions;
    private readonly HashSet<string> _contentTypes;

    /// <summary>Creates the adapter.</summary>
    /// <param name="reader">The Microsoft.Extensions.DataIngestion reader.</param>
    /// <param name="fileExtensions">File extensions this reader handles, with their dot, such as <c>.md</c>. Matching ignores case.</param>
    /// <param name="contentTypes">Content types this reader handles, such as <c>text/markdown</c>. Matching ignores case and parameters.</param>
    /// <exception cref="ArgumentException">Neither an extension nor a content type is given.</exception>
    public DataIngestionReader(IngestionDocumentReader reader, IEnumerable<string> fileExtensions, IEnumerable<string>? contentTypes = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(fileExtensions);
        _reader = reader;
        _extensions = new HashSet<string>(fileExtensions.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()), StringComparer.OrdinalIgnoreCase);
        _contentTypes = new HashSet<string>(
            (contentTypes ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        if (_extensions.Count == 0 && _contentTypes.Count == 0)
        {
            throw new ArgumentException("Give at least one file extension or content type for the reader to handle.", nameof(fileExtensions));
        }
    }

    /// <inheritdoc />
    public bool CanRead(string? contentType, string fileName)
    {
        if (contentType is not null)
        {
            var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
            var bare = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
            if (_contentTypes.Contains(bare)) return true;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && _extensions.Contains(extension);
    }

    /// <inheritdoc />
    public async Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Microsoft's readers can throw on empty content; an empty document is the Ingestor's EmptyDocument warning instead.
        if (stream.CanSeek && stream.Length - stream.Position == 0) return new ParsedDocument([]);

        IngestionDocument document;
        try
        {
            document = await _reader.ReadAsync(stream, context.FileName, context.ContentType ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Any reader failure is this document's read error.
        catch (Exception e)
#pragma warning restore CA1031
        {
            throw new DocumentReadException(context.FileName, $"{e.GetType().Name}: {e.Message}", e);
        }

        return Map(document, context);
    }

    private static ParsedDocument Map(IngestionDocument document, DocumentReadContext context)
    {
        var blocks = new List<DocumentBlock>();
        var candidates = new List<OcrCandidate>();
        var path = new List<(int Level, string Title)>();
        string? title = null;

        void Add(BlockKind kind, string? text, int? page)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;
            blocks.Add(new DocumentBlock(kind, trimmed, path.Select(h => h.Title).ToArray(), page));
        }

        void Walk(IngestionDocumentElement element)
        {
            switch (element)
            {
                case IngestionDocumentSection section:
                    foreach (var child in section.Elements) Walk(child);
                    break;

                case IngestionDocumentHeader header:
                    var markdown = header.GetMarkdown()?.Trim() ?? string.Empty;
                    var headingText = string.IsNullOrWhiteSpace(header.Text) ? markdown.TrimStart('#').Trim() : header.Text.Trim();
                    if (headingText.Length == 0) break;
                    var level = header.Level ?? 1;
                    path.RemoveAll(h => h.Level >= level);
                    path.Add((level, headingText));
                    if (level == 1 && title is null) title = headingText;
                    Add(BlockKind.Heading, markdown.Length > 0 ? markdown : headingText, header.PageNumber);
                    break;

                case IngestionDocumentTable table:
                    Add(BlockKind.Table, table.GetMarkdown(), table.PageNumber);
                    break;

                case IngestionDocumentImage image when image.Content is { } bytes:
                    if (context.ExtractImages)
                    {
                        candidates.Add(new OcrCandidate(
                            OcrCandidateKind.Image, blocks.Count, image.PageNumber,
                            new ImageInput(bytes, string.IsNullOrWhiteSpace(image.MediaType) ? "application/octet-stream" : image.MediaType)));
                    }

                    Add(BlockKind.Paragraph, image.AlternativeText, image.PageNumber);
                    break;

                default:
                    var text = element.GetMarkdown();
                    Add(BlockKind.Paragraph, string.IsNullOrWhiteSpace(text) ? element.Text : text, element.PageNumber);
                    break;
            }
        }

        foreach (var section in document.Sections) Walk(section);
        return new ParsedDocument(blocks, title) { OcrCandidates = candidates };
    }
}
