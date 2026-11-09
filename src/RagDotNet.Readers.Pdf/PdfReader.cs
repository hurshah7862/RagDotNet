using System.Text.RegularExpressions;
using RagDotNet.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace RagDotNet.Readers.Pdf;

/// <summary>
/// Reads PDF text into blocks that carry their page number. Headings are recognised by font size (at least 1.15× the
/// document's most common size, at most two lines), code by a monospace font, and lists by bullet or number markers.
/// Tables are read as plain text. A page with no text, or images on a page with text, are reported as warnings so that
/// nothing is skipped silently.
/// </summary>
public sealed partial class PdfReader : IDocumentReader
{
    private const double HeadingRatio = 1.15;

    /// <inheritdoc />
    public bool CanRead(string? contentType, string fileName) =>
        contentType is not null
            ? string.Equals(contentType.Split(';')[0].Trim(), "application/pdf", StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);

        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        try
        {
            using var pdf = PdfDocument.Open(buffer.ToArray());
            return Read(pdf, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DocumentReadException)
        {
            throw new DocumentReadException(context.FileName, "the file is not a readable PDF document (it may be corrupt or password-protected)", ex);
        }
    }

    private static ParsedDocument Read(PdfDocument pdf, CancellationToken ct)
    {
        var warnings = new List<IngestWarning>();
        var pages = new List<(int Number, IReadOnlyList<TextBlock> Blocks)>();

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (letters.Count == 0)
            {
                warnings.Add(new IngestWarning(IngestWarningCodes.PageWithoutText,
                    $"Page {page.Number} has no extractable text; register an OCR provider to read it.", page.Number));
                continue;
            }

            var images = page.GetImages().Count();
            if (images > 0)
            {
                warnings.Add(new IngestWarning(IngestWarningCodes.ImageNotRead,
                    $"Page {page.Number} has {images} image(s) that were not read; register an OCR provider to read them.", page.Number));
            }

            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            pages.Add((page.Number, UnsupervisedReadingOrderDetector.Instance.Get(blocks).ToList()));
        }

        var bodySize = BodySize(pages.SelectMany(p => p.Blocks));
        var headingSizes = pages.SelectMany(p => p.Blocks)
            .Where(b => IsHeading(b, bodySize))
            .Select(b => RoundHalf(AverageSize(b)))
            .Distinct()
            .OrderDescending()
            .ToList();

        var result = new List<DocumentBlock>();
        var path = new List<(int Level, string Title)>();
        foreach (var (number, blocks) in pages)
        {
            foreach (var block in blocks)
            {
                if (IsHeading(block, bodySize))
                {
                    var level = headingSizes.IndexOf(RoundHalf(AverageSize(block))) + 1;
                    var title = Join(block, " ");
                    path.RemoveAll(h => h.Level >= level);
                    path.Add((level, title));
                    result.Add(new DocumentBlock(BlockKind.Heading, title, Snapshot(path), number));
                }
                else if (IsCode(block))
                {
                    result.Add(new DocumentBlock(BlockKind.Code, Join(block, "\n"), Snapshot(path), number));
                }
                else if (ListText(block) is { } list)
                {
                    result.Add(new DocumentBlock(BlockKind.List, list, Snapshot(path), number));
                }
                else
                {
                    result.Add(new DocumentBlock(BlockKind.Paragraph, Join(block, " "), Snapshot(path), number));
                }
            }
        }

        var docTitle = pdf.Information.Title;
        if (string.IsNullOrWhiteSpace(docTitle))
            docTitle = result.FirstOrDefault(b => b.Kind == BlockKind.Heading && b.HeadingPath.Count == 1)?.Text;
        return new ParsedDocument(result.Where(b => b.Text.Length > 0).ToList(), string.IsNullOrWhiteSpace(docTitle) ? null : docTitle.Trim())
        {
            Warnings = warnings,
        };
    }

    private static string[] Snapshot(List<(int Level, string Title)> path) => [.. path.Select(h => h.Title)];

    private static IEnumerable<Letter> Letters(TextBlock block) =>
        block.TextLines.SelectMany(l => l.Words).SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value));

    /// <summary>The most common letter size in the document, rounded to half a point.</summary>
    private static double BodySize(IEnumerable<TextBlock> blocks)
    {
        var sizes = blocks.SelectMany(Letters).Select(l => RoundHalf(l.PointSize)).ToList();
        return sizes.Count == 0 ? 0 : sizes.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
    }

    private static double AverageSize(TextBlock block)
    {
        var letters = Letters(block).ToList();
        return letters.Count == 0 ? 0 : letters.Average(l => l.PointSize);
    }

    private static double RoundHalf(double size) => Math.Round(size * 2, MidpointRounding.AwayFromZero) / 2;

    private static bool IsHeading(TextBlock block, double bodySize) =>
        bodySize > 0 && block.TextLines.Count <= 2 && AverageSize(block) >= bodySize * HeadingRatio && !IsCode(block);

    private static bool IsCode(TextBlock block)
    {
        var letters = Letters(block).ToList();
        return letters.Count > 0 && letters.All(l => IsMonospace(l.FontName));
    }

    private static bool IsMonospace(string? font) =>
        font is not null && (font.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || font.Contains("Mono", StringComparison.OrdinalIgnoreCase)
            || font.Contains("Consolas", StringComparison.OrdinalIgnoreCase));

    private static string Join(TextBlock block, string separator) =>
        string.Join(separator, block.TextLines.Select(l => l.Text.Trim()).Where(t => t.Length > 0));

    /// <summary>
    /// A block whose first line starts with a list marker is a list: each marked line starts an item and an unmarked
    /// line continues the previous item (a wrapped item). Items are rewritten as <c>- item</c>.
    /// </summary>
    private static string? ListText(TextBlock block)
    {
        var lines = block.TextLines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();
        if (lines.Count == 0 || !ListMarker().IsMatch(lines[0])) return null;

        var items = new List<string>();
        foreach (var line in lines)
        {
            var m = ListMarker().Match(line);
            if (m.Success) items.Add(line[m.Length..].Trim());
            else items[^1] += " " + line;
        }

        return string.Join('\n', items.Select(i => "- " + i));
    }

    [GeneratedRegex(@"^(?:[•\-–*]|\d+[.)])\s+")]
    private static partial Regex ListMarker();
}
