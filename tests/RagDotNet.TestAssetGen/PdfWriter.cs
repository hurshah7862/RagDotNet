using RagDotNet.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RagDotNet.TestAssetGen;

/// <summary>
/// Writes text PDFs with the standard 14 fonts: Helvetica 11 pt body, bold 18/15/13 pt headings, Courier 9.5 pt code.
/// Lines inside a block are 1.3× the font size apart and blocks are separated by an extra 1.5× gap, so layout analysis
/// keeps each source block as one PDF block.
/// </summary>
internal sealed class PdfWriter
{
    private const double PageWidth = 595, PageHeight = 842, Margin = 56;
    private const double BodySize = 11, CodeSize = 9.5;
    private static readonly double[] HeadingSizes = [18, 15, 13, 12];

    private readonly PdfDocumentBuilder _builder = new();
    private readonly PdfDocumentBuilder.AddedFont _regular, _bold, _mono;
    private PdfPageBuilder _page;
    private double _y;

    private PdfWriter(string? title)
    {
        _regular = _builder.AddStandard14Font(Standard14Font.Helvetica);
        _bold = _builder.AddStandard14Font(Standard14Font.HelveticaBold);
        _mono = _builder.AddStandard14Font(Standard14Font.Courier);
        if (title is not null) _builder.DocumentInformation.Title = title;
        _builder.DocumentInformation.Producer = "RagDotNet.TestAssetGen";
        _page = _builder.AddPage(PageWidth, PageHeight);
        _y = PageHeight - Margin;
    }

    /// <summary>Writes <paramref name="blocks"/> as a PDF.</summary>
    public static void Write(string path, IReadOnlyList<GenBlock> blocks, string? title)
    {
        var w = new PdfWriter(title);
        foreach (var block in blocks) w.Block(block);
        w.Save(path);
    }

    /// <summary>
    /// The reader-test sample. Page 1: headings, paragraphs, a bullet list and a code block. Page 2: only an image (a
    /// "scanned" page with no text). Page 3: a heading, text and a small image.
    /// </summary>
    public static void WriteSample(string path)
    {
        var w = new PdfWriter("Quill for Teams");
        w.Block(GenBlock.Heading(1, "Quill for Teams"));
        w.Block(GenBlock.Paragraph("Quill for Teams adds shared notebooks, roles and an admin console to the desktop and mobile apps. Every member keeps their own private notes as well."));
        w.Block(GenBlock.Heading(2, "Sharing a notebook"));
        w.Block(GenBlock.Paragraph("You can share a notebook with up to fifty people. Each person gets one of three roles, and the owner can change a role at any time from the sharing panel."));
        w.Block(GenBlock.List((0, "Owners can invite and remove members."), (0, "Editors can change any note."), (0, "Viewers can read notes but not change them.")));
        w.Block(GenBlock.Code("quill share --notebook Recipes\nquill members --notebook Recipes"));

        w.NewPage();
        w._page.AddPng(Png.Solid(400, 560, 0xC8), new PdfRectangle(Margin, Margin + 100, PageWidth - Margin, PageHeight - Margin));

        w.NewPage();
        w.Block(GenBlock.Heading(2, "Offline access"));
        w.Block(GenBlock.Paragraph("Shared notebooks are cached for thirty days on each device, so members can read and edit them without a connection."));
        w._page.AddPng(Png.Solid(120, 60, 0x60), new PdfRectangle(Margin, w._y - 60, Margin + 120, w._y));
        w._y -= 60 + BodySize * 1.5;
        w.Block(GenBlock.Paragraph("Edits made offline upload when the device reconnects."));
        w.Save(path);
    }

    private void Block(GenBlock block)
    {
        switch (block.Kind)
        {
            case BlockKind.Heading:
                var size = HeadingSizes[Math.Clamp(block.Level, 1, HeadingSizes.Length) - 1];
                Lines(Wrap(block.Text, _bold, size, Width()), _bold, size, Margin);
                break;
            case BlockKind.Paragraph:
                Lines(Wrap(block.Text, _regular, BodySize, Width()), _regular, BodySize, Margin);
                break;
            case BlockKind.Code:
                Lines(block.Text.Split('\n').SelectMany(l => Wrap(l, _mono, CodeSize, Width())).ToList(), _mono, CodeSize, Margin);
                break;
            case BlockKind.List:
                var lines = new List<(string Text, double X)>();
                foreach (var (depth, text) in block.Items!)
                {
                    var indent = Margin + depth * 14;
                    var wrapped = Wrap("• " + text, _regular, BodySize, PageWidth - Margin - indent);
                    lines.Add((wrapped[0], indent));
                    lines.AddRange(wrapped.Skip(1).Select(l => (l, indent + 10)));
                }

                Lines(lines, _regular, BodySize);
                break;
            case BlockKind.Table:
                Lines(block.Rows!.SelectMany(r => Wrap(string.Join(" | ", r), _regular, BodySize, Width())).ToList(), _regular, BodySize, Margin);
                break;
        }
    }

    private static double Width() => PageWidth - 2 * Margin;

    private void Lines(IReadOnlyList<string> lines, PdfDocumentBuilder.AddedFont font, double size, double x) =>
        Lines(lines.Select(l => (l, x)).ToList(), font, size);

    private void Lines(IReadOnlyList<(string Text, double X)> lines, PdfDocumentBuilder.AddedFont font, double size)
    {
        var advance = size * 1.3;
        if (_y - advance * Math.Min(lines.Count, 3) < Margin) NewPage();
        foreach (var (text, x) in lines)
        {
            if (_y - advance < Margin) NewPage();
            _y -= advance;
            if (text.Length > 0) _page.AddText(text, size, new PdfPoint(x, _y), font);
        }

        _y -= size * 1.5;
    }

    private List<string> Wrap(string text, PdfDocumentBuilder.AddedFont font, double size, double width)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && Measure(candidate, font, size) > width)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        lines.Add(current);
        return lines;
    }

    private double Measure(string text, PdfDocumentBuilder.AddedFont font, double size)
    {
        var letters = _page.MeasureText(text, size, new PdfPoint(0, 0), font);
        return letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
    }

    private void NewPage()
    {
        _page = _builder.AddPage(PageWidth, PageHeight);
        _y = PageHeight - Margin;
    }

    private void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, _builder.Build());
    }
}
