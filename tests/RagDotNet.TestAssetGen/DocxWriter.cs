using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using RagDotNet.Documents;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace RagDotNet.TestAssetGen;

/// <summary>Writes DOCX files with built-in heading styles, numbered lists, real tables and a Consolas "Code" style.</summary>
internal static class DocxWriter
{
    private const int ListNumId = 1;

    /// <summary>Writes <paramref name="blocks"/> as a DOCX document.</summary>
    public static void Write(string path, IReadOnlyList<GenBlock> blocks, string? title)
    {
        using var doc = Create(path, title);
        var body = doc.MainDocumentPart!.Document!.Body!;
        foreach (var block in blocks) Append(body, block);
    }

    /// <summary>
    /// The reader-test sample: a Title, Heading1/Heading2, a custom "Chapter" style that is a heading only through its
    /// outline level, a paragraph with a tab and a line break, a two-level numbered list, a 3×2 table, two Code
    /// paragraphs, an inline PNG and a tracked deletion.
    /// </summary>
    public static void WriteSample(string path)
    {
        using var doc = Create(path, title: null);
        var main = doc.MainDocumentPart!;
        var body = main.Document!.Body!;

        body.Append(Para("Title", "Quill Desktop Guide"));
        body.Append(Para("Heading1", "Getting started"));
        body.Append(new Paragraph(
            new Run(new Text("Open Quill and choose a vault.")),
            new Run(new TabChar(), new Text("Then press Start.")),
            new Run(new Break(), new Text("Your notes appear in the sidebar.") { Space = SpaceProcessingModeValues.Preserve })));
        body.Append(Para("Heading2", "Keyboard basics"));
        body.Append(ListPara(0, "Create a note"));
        body.Append(ListPara(1, "Press Ctrl+N"));
        body.Append(ListPara(1, "Type a title"));
        body.Append(ListPara(0, "Search notes"));
        Append(body, GenBlock.Table(["Action", "Shortcut"], ["New note", "Ctrl+N"], ["Search", "Ctrl+Shift+F"]));
        body.Append(Para("Chapter", "Working offline"));
        body.Append(new Paragraph(
            new Run(new Text("Quill works without a connection.") { Space = SpaceProcessingModeValues.Preserve }),
            new DeletedRun(new Run(new DeletedText("DELETED_TEXT removed sentence.")))
            {
                Id = "1", Author = "Editor", Date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            }));
        body.Append(Para("Code", "quill sync --now"));
        body.Append(Para("Code", "quill status"));
        body.Append(new Paragraph(new Run(Image(main, Png.Solid(40, 20, 0x80)))));
        body.Append(new Paragraph(new Run(new Text("Changes upload when you reconnect."))));
    }

    private static WordprocessingDocument Create(string path, string? title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        main.AddNewPart<StyleDefinitionsPart>().Styles = Styles();
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = Numbering();
        if (title is not null) doc.PackageProperties.Title = title;

        // Fixed timestamps keep regenerated files' metadata stable.
        doc.PackageProperties.Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        doc.PackageProperties.Modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return doc;
    }

    private static void Append(Body body, GenBlock block)
    {
        switch (block.Kind)
        {
            case BlockKind.Heading:
                body.Append(Para($"Heading{Math.Clamp(block.Level, 1, 9)}", block.Text));
                break;
            case BlockKind.Paragraph:
                body.Append(Para(null, block.Text));
                break;
            case BlockKind.Code:
                foreach (var line in block.Text.Split('\n')) body.Append(Para("Code", line));
                break;
            case BlockKind.List:
                foreach (var (depth, text) in block.Items!) body.Append(ListPara(depth, text));
                break;
            case BlockKind.Table:
                var table = new Table(new TableProperties(new TableBorders(
                    new TopBorder { Val = BorderValues.Single }, new BottomBorder { Val = BorderValues.Single },
                    new InsideHorizontalBorder { Val = BorderValues.Single }, new InsideVerticalBorder { Val = BorderValues.Single })));
                foreach (var row in block.Rows!)
                    table.Append(new TableRow(row.Select(cell => new TableCell(Para(null, cell)))));
                body.Append(table);
                body.Append(new Paragraph()); // Word needs a paragraph between consecutive tables.
                break;
        }
    }

    private static Paragraph Para(string? styleId, string text)
    {
        var paragraph = new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        if (styleId is not null) paragraph.PrependChild(new ParagraphProperties(new ParagraphStyleId { Val = styleId }));
        return paragraph;
    }

    private static Paragraph ListPara(int level, string text) =>
        new(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "ListParagraph" },
                new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = ListNumId })),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Styles Styles()
    {
        var styles = new Styles(
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            Heading("Title", "Title", 0, 28),
            Heading("Heading1", "heading 1", 0, 20),
            Heading("Heading2", "heading 2", 1, 16),
            Heading("Heading3", "heading 3", 2, 13),
            // A custom style that is a heading only because of its outline level.
            Heading("Chapter", "Chapter", 0, 18),
            new Style(new StyleName { Val = "List Paragraph" }, new BasedOn { Val = "Normal" })
            {
                Type = StyleValues.Paragraph, StyleId = "ListParagraph",
            },
            new Style(
                new StyleName { Val = "Code" }, new BasedOn { Val = "Normal" },
                new StyleRunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }, new FontSize { Val = "19" }))
            {
                Type = StyleValues.Paragraph, StyleId = "Code",
            });
        return styles;

        static Style Heading(string id, string name, int outline, int halfPoints) =>
            new(
                new StyleName { Val = name }, new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(new OutlineLevel { Val = outline }),
                new StyleRunProperties(new Bold(), new FontSize { Val = (halfPoints * 2).ToString(System.Globalization.CultureInfo.InvariantCulture) }))
            {
                Type = StyleValues.Paragraph, StyleId = id,
            };
    }

    private static Numbering Numbering() =>
        new(
            new AbstractNum(
                Level(0, "%1."), Level(1, "%2."))
            { AbstractNumberId = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = ListNumId });

    private static Level Level(int ilvl, string text) =>
        new(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = NumberFormatValues.Decimal },
            new LevelText { Val = text },
            new PreviousParagraphProperties(new Indentation { Left = (720 * (ilvl + 1)).ToString(System.Globalization.CultureInfo.InvariantCulture), Hanging = "360" }))
        {
            LevelIndex = ilvl,
        };

    private static Drawing Image(MainDocumentPart main, byte[] png)
    {
        var part = main.AddImagePart(ImagePartType.Png);
        using (var s = new MemoryStream(png)) part.FeedData(s);
        var id = main.GetIdOfPart(part);
        const long cx = 381000, cy = 190500; // 40×20 px at 96 dpi, in EMUs

        return new Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.DocProperties { Id = 1U, Name = "Picture 1" },
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = "image.png" },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = id }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U,
        });
    }
}
