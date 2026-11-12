using System.Text.RegularExpressions;
using RagDotNet.Documents;
using RagDotNet.Ocr;
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
/// Tables are read as plain text. Pages with no text and images on pages with text are returned as
/// <see cref="ParsedDocument.OcrCandidates"/>, which ingest reads with an OCR provider or reports as warnings.
/// </summary>
public sealed partial class PdfReader : IDocumentReader
{
    private const double HeadingRatio = 1.15;

    /// <summary>An image covering at least this share of a page with a text layer is the scan under that text.</summary>
    private const double BackgroundShare = 0.8;

    /// <summary>A page with a background image and at most this many characters is a scan with a stamp, such as a page number.</summary>
    private const int StampLetters = 30;

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
        var pages = new List<(int Number, IReadOnlyList<Lines> Blocks)>();
        var pageImages = new List<PageImages>();

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (letters.Count == 0)
            {
                pageImages.Add(new PageImages(page.Number, Scanned: true, Stamp: false, []));
                continue;
            }

            var pageArea = page.Width * page.Height;
            var images = page.GetImages()
                .Select(i => (Extract(i), i.BoundingBox.Top, i.BoundingBox.Width * i.BoundingBox.Height >= BackgroundShare * pageArea))
                .ToList();
            if (images.Count > 0)
            {
                // A few characters over a page-sized image, such as a page-number stamp, mean the page is really a scan.
                var stamp = letters.Count <= StampLetters && images.Any(i => i.Item3);
                pageImages.Add(new PageImages(page.Number, Scanned: false, stamp, images));
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

        var result = new List<Placed>();
        var path = new List<(int Level, string Title)>();
        foreach (var (number, blocks) in pages)
        {
            foreach (var block in blocks)
            {
                var top = block.TextLines.Max(l => l.BoundingBox.Top);
                if (IsHeading(block, bodySize))
                {
                    var level = headingSizes.IndexOf(RoundHalf(AverageSize(block))) + 1;
                    var title = Join(block, " ");
                    path.RemoveAll(h => h.Level >= level);
                    path.Add((level, title));
                    result.Add(new Placed(new DocumentBlock(BlockKind.Heading, title, Snapshot(path), number), top));
                }
                else if (IsCode(block))
                {
                    result.Add(new Placed(new DocumentBlock(BlockKind.Code, Join(block, "\n"), Snapshot(path), number), top));
                }
                else if (ListText(block) is { } list)
                {
                    result.Add(new Placed(new DocumentBlock(BlockKind.List, list, Snapshot(path), number), top));
                }
                else
                {
                    result.Add(new Placed(new DocumentBlock(BlockKind.Paragraph, Join(block, " "), Snapshot(path), number), top));
                }
            }
        }

        var placed = MergeNumberedRuns(result).Where(p => p.Block.Text.Length > 0).ToList();

        var docTitle = pdf.Information.Title;
        if (string.IsNullOrWhiteSpace(docTitle))
            docTitle = placed.FirstOrDefault(p => p.Block.Kind == BlockKind.Heading && p.Block.HeadingPath.Count == 1)?.Block.Text;
        return new ParsedDocument([.. placed.Select(p => p.Block)], string.IsNullOrWhiteSpace(docTitle) ? null : docTitle.Trim())
        {
            OcrCandidates = Candidates(placed, pageImages),
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
    private static List<Placed> MergeNumberedRuns(List<Placed> blocks)
    {
        var merged = new List<Placed>();
        foreach (var placed in blocks)
        {
            var block = placed.Block;
            if (merged.Count > 0 && merged[^1].Block is var previous
                && previous.PageNumber == block.PageNumber && previous.HeadingPath.SequenceEqual(block.HeadingPath)
                && NumberedLines(previous) is { } before && NumberedLines(block) is { } after
                && after[0] == before[^1] + 1)
            {
                merged[^1] = merged[^1] with { Block = previous with { Kind = BlockKind.List, Text = previous.Text + "\n" + block.Text } };
                continue;
            }

            merged.Add(placed);
        }

        return merged;
    }

    /// <summary>A block with the top of its first line on the page, in PDF points (y grows upwards).</summary>
    private sealed record Placed(DocumentBlock Block, double Top);

    /// <summary>What a page holds that only OCR can read.</summary>
    private sealed record PageImages(int Number, bool Scanned, bool Stamp, IReadOnlyList<(ImageInput? Image, double Top, bool Background)> Images);

    /// <summary>
    /// Candidates in page order. A scanned page's text goes where its blocks would be; an image's text goes after the last
    /// block on its page that starts above it; a stamp page's OCR text goes after the page's own blocks.
    /// </summary>
    private static List<OcrCandidate> Candidates(List<Placed> placed, List<PageImages> pages)
    {
        var candidates = new List<OcrCandidate>();
        foreach (var page in pages)
        {
            var pageStart = placed.FindIndex(p => p.Block.PageNumber >= page.Number);
            if (pageStart < 0) pageStart = placed.Count;
            var pageEnd = placed.FindIndex(p => p.Block.PageNumber > page.Number);
            if (pageEnd < 0) pageEnd = placed.Count;

            if (page.Scanned)
            {
                candidates.Add(new OcrCandidate(OcrCandidateKind.ScannedPage, pageStart, page.Number));
                continue;
            }

            if (page.Stamp)
                candidates.Add(new OcrCandidate(OcrCandidateKind.ScannedPage, pageEnd, page.Number) { HasTextLayer = true });

            foreach (var (image, top, background) in page.Images)
            {
                var insertAt = pageStart;
                for (var i = pageStart; i < pageEnd; i++)
                    if (placed[i].Top > top) insertAt = i + 1;
                candidates.Add(new OcrCandidate(OcrCandidateKind.Image, insertAt, page.Number, image) { IsPageBackground = background });
            }
        }

        return candidates;
    }

    /// <summary>The image as PNG, or as JPEG when it is stored as one; null when neither can be had.</summary>
    private static ImageInput? Extract(IPdfImage image)
    {
        try
        {
            if (image.TryGetPng(out var png))
                return new ImageInput(png, "image/png", image.WidthInSamples, image.HeightInSamples);
            var raw = image.RawMemory;
            if (raw.Length > 2 && raw.Span[0] == 0xFF && raw.Span[1] == 0xD8)
                return new ImageInput(raw.ToArray(), "image/jpeg", image.WidthInSamples, image.HeightInSamples);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An image PdfPig cannot decode is reported by ingest as one that could not be extracted.
        }

        return null;
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
