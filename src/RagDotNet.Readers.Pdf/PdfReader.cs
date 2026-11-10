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

        // PdfPig needs a seekable stream; one copy in memory, read in place.
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        try
        {
            using var pdf = PdfDocument.Open(buffer);
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
        var pages = new List<(int Number, IReadOnlyList<Lines> Blocks)>();

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
            pages.Add((page.Number, UnsupervisedReadingOrderDetector.Instance.Get(blocks).SelectMany(SplitAtSizeChanges).ToList()));
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

        result = MergeNumberedRuns(result);

        var docTitle = pdf.Information.Title;
        if (string.IsNullOrWhiteSpace(docTitle))
            docTitle = result.FirstOrDefault(b => b.Kind == BlockKind.Heading && b.HeadingPath.Count == 1)?.Text;
        return new ParsedDocument(result.Where(b => b.Text.Length > 0).ToList(), string.IsNullOrWhiteSpace(docTitle) ? null : docTitle.Trim())
        {
            Warnings = warnings,
        };
    }

    private static string[] Snapshot(List<(int Level, string Title)> path) => [.. path.Select(h => h.Title)];

    /// <summary>The lines of one text block, or of one part of it after <see cref="SplitAtSizeChanges"/>.</summary>
    private sealed record Lines(IReadOnlyList<TextLine> TextLines);

    /// <summary>
    /// Layout analysis can put a heading set close above its text into the same block. Splitting wherever the font size
    /// between neighbouring lines changes by the heading ratio or more keeps the heading on its own.
    /// </summary>
    private static IEnumerable<Lines> SplitAtSizeChanges(TextBlock block)
    {
        var current = new List<TextLine>();
        double? previous = null;
        foreach (var line in block.TextLines)
        {
            var size = LineSize(line);
            if (previous is { } p && size > 0 && p > 0 && Math.Max(size, p) / Math.Min(size, p) >= HeadingRatio && current.Count > 0)
            {
                yield return new Lines(current);
                current = [];
            }

            current.Add(line);
            if (size > 0) previous = size;
        }

        if (current.Count > 0) yield return new Lines(current);
    }

    private static double LineSize(TextLine line)
    {
        var letters = line.Words.SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        return letters.Count == 0 ? 0 : letters.Average(l => l.PointSize);
    }

    private static IEnumerable<Letter> Letters(Lines block) =>
        block.TextLines.SelectMany(l => l.Words).SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value));

    /// <summary>The most common letter size in the document, rounded to half a point.</summary>
    private static double BodySize(IEnumerable<Lines> blocks)
    {
        var sizes = blocks.SelectMany(Letters).Select(l => RoundHalf(l.PointSize)).ToList();
        return sizes.Count == 0 ? 0 : sizes.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
    }

    private static double AverageSize(Lines block)
    {
        var letters = Letters(block).ToList();
        return letters.Count == 0 ? 0 : letters.Average(l => l.PointSize);
    }

    private static double RoundHalf(double size) => Math.Round(size * 2, MidpointRounding.AwayFromZero) / 2;

    private static bool IsHeading(Lines block, double bodySize) =>
        bodySize > 0 && block.TextLines.Count <= 2 && AverageSize(block) >= bodySize * HeadingRatio && !IsCode(block);

    private static bool IsCode(Lines block)
    {
        var letters = Letters(block).ToList();
        return letters.Count > 0 && letters.All(l => IsMonospace(l.FontName));
    }

    private static bool IsMonospace(string? font) =>
        font is not null && (font.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || font.Contains("Mono", StringComparison.OrdinalIgnoreCase)
            || font.Contains("Consolas", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Joins the block's lines. With a space separator (prose), a word hyphenated at a line end is rejoined when the
    /// next line continues it in lower case ("appli-" + "cation"); a hyphen before a capital is kept.
    /// </summary>
    private static string Join(Lines block, string separator)
    {
        var lines = block.TextLines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();
        if (separator != " " || lines.Count == 0) return string.Join(separator, lines);

        var sb = new System.Text.StringBuilder(lines[0]);
        for (var i = 1; i < lines.Count; i++)
        {
            var hyphenated = sb.Length > 1 && sb[^1] == '-' && char.IsLetter(sb[^2]) && char.IsLower(lines[i][0]);
            if (hyphenated) sb.Length--;
            else sb.Append(' ');
            sb.Append(lines[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// A block whose first line starts with a list marker is a list: each marked line starts an item and an unmarked
    /// line continues the previous item (a wrapped item). Bullet items are rewritten as <c>- item</c>; numbered items
    /// keep their number, as in Markdown. A number alone is not enough: a block counts as a numbered list only when at
    /// least two lines start with a number, so a paragraph such as "2026. was a good year" stays a paragraph.
    /// </summary>
    private static string? ListText(Lines block)
    {
        var lines = block.TextLines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();
        if (lines.Count == 0) return null;
        if (BulletMarker().IsMatch(lines[0])) { }
        else if (!NumberMarker().IsMatch(lines[0]) || lines.Count(l => NumberMarker().IsMatch(l)) < 2) return null;

        var items = new List<string>();
        foreach (var line in lines)
        {
            if (BulletMarker().Match(line) is { Success: true } bullet) items.Add("- " + line[bullet.Length..].Trim());
            else if (NumberMarker().IsMatch(line)) items.Add(line);
            else items[^1] += " " + line;
        }

        return string.Join('\n', items);
    }

    /// <summary>
    /// Layout analysis puts numbered items spaced far apart into blocks of their own, each too short to count as a list.
    /// Neighbouring numbered blocks on the same page under the same heading, numbered one after the other (1., 2., 3.),
    /// become one list block.
    /// </summary>
    private static List<DocumentBlock> MergeNumberedRuns(List<DocumentBlock> blocks)
    {
        var merged = new List<DocumentBlock>();
        foreach (var block in blocks)
        {
            if (merged.Count > 0 && merged[^1] is var previous
                && previous.PageNumber == block.PageNumber && previous.HeadingPath.SequenceEqual(block.HeadingPath)
                && NumberedLines(previous) is { } before && NumberedLines(block) is { } after
                && after[0] == before[^1] + 1)
            {
                merged[^1] = previous with { Kind = BlockKind.List, Text = previous.Text + "\n" + block.Text };
                continue;
            }

            merged.Add(block);
        }

        return merged;
    }

    /// <summary>The item numbers of a paragraph or list block whose every line is numbered; otherwise null.</summary>
    private static List<int>? NumberedLines(DocumentBlock block)
    {
        if (block.Kind is not (BlockKind.Paragraph or BlockKind.List)) return null;
        var numbers = block.Text.Split('\n').Select(Number).ToList();
        return numbers.All(n => n is not null) ? numbers.Select(n => n!.Value).ToList() : null;
    }

    private static int? Number(string line) =>
        NumberMarker().Match(line) is { Success: true } m && int.TryParse(m.Value.TrimEnd().TrimEnd('.', ')'), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    [GeneratedRegex(@"^[•\-–*]\s+")]
    private static partial Regex BulletMarker();

    [GeneratedRegex(@"^\d+[.)]\s+")]
    private static partial Regex NumberMarker();
}
