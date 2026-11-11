using RagDotNet.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Readers.Docx;
using static RagDotNet.Readers.Tests.ReaderTestKit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace RagDotNet.Readers.Tests;

public class DocxReaderTests
{
    private static readonly DocxReader Reader = new();

    private static string SamplePath => Asset("docx", "sample.docx");

    private static Task<ParsedDocument> Sample() => ReadFileAsync(Reader, SamplePath);

    [Fact] // Review Focus 4
    public async Task Headings_come_from_heading_styles_and_outline_levels()
    {
        var headings = (await Sample()).Blocks.Where(b => b.Kind == BlockKind.Heading).ToList();

        Assert.Equal(["Quill Desktop Guide", "Getting started", "Keyboard basics", "Working offline"], headings.Select(h => h.Text));
        Assert.Equal(["Getting started", "Keyboard basics"], headings[2].HeadingPath);
        Assert.Equal(["Working offline"], headings[3].HeadingPath); // custom "Chapter" style, outline level 0
    }

    [Fact]
    public async Task Title_style_sets_the_document_title() =>
        Assert.Equal("Quill Desktop Guide", (await Sample()).Title);

    [Fact]
    public async Task Tabs_and_line_breaks_are_kept_as_space_and_newline()
    {
        var doc = await Sample();

        Assert.Contains(doc.Blocks, b => b.Kind == BlockKind.Paragraph
            && b.Text == "Open Quill and choose a vault. Then press Start.\nYour notes appear in the sidebar."
            && b.HeadingPath.SequenceEqual(["Getting started"]));
    }

    [Fact] // Review Focus 4
    public async Task Numbered_paragraphs_become_one_list_block_indented_by_level()
    {
        var list = Assert.Single((await Sample()).Blocks, b => b.Kind == BlockKind.List);

        Assert.Equal("- Create a note\n  - Press Ctrl+N\n  - Type a title\n- Search notes", list.Text);
        Assert.Equal(["Getting started", "Keyboard basics"], list.HeadingPath);
    }

    [Fact] // Review Focus 4
    public async Task Tables_become_table_blocks_with_one_line_per_row()
    {
        var table = Assert.Single((await Sample()).Blocks, b => b.Kind == BlockKind.Table);

        Assert.Equal("| Action | Shortcut |\n| New note | Ctrl+N |\n| Search | Ctrl+Shift+F |", table.Text);
    }

    [Fact]
    public async Task Code_style_paragraphs_become_one_code_block_with_line_breaks()
    {
        var code = Assert.Single((await Sample()).Blocks, b => b.Kind == BlockKind.Code);

        Assert.Equal("quill sync --now\nquill status", code.Text);
        Assert.Equal(["Working offline"], code.HeadingPath);
    }

    [Fact]
    public async Task Deleted_text_is_ignored()
    {
        var doc = await Sample();

        Assert.DoesNotContain(doc.Blocks, b => b.Text.Contains("DELETED_TEXT", StringComparison.Ordinal));
        Assert.Contains(doc.Blocks, b => b.Text == "Quill works without a connection.");
    }

    [Fact] // Final review: Word stores each text box twice (modern + VML fallback); both were glued onto the anchor paragraph
    public async Task Text_boxes_are_not_read_and_do_not_leak_into_paragraphs()
    {
        using var ms = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var box = () => new DocumentFormat.OpenXml.Wordprocessing.TextBoxContent(
                new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("TEXTBOX text"))));
            doc.AddMainDocumentPart().Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
                new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Anchor text.")),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Picture(
                        new DocumentFormat.OpenXml.Vml.Shape(new DocumentFormat.OpenXml.Vml.TextBox(box())))),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Picture(
                        new DocumentFormat.OpenXml.Vml.Shape(new DocumentFormat.OpenXml.Vml.TextBox(box()))))),
                new DocumentFormat.OpenXml.Wordprocessing.Table(new DocumentFormat.OpenXml.Wordprocessing.TableRow(new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Cell text.")),
                        new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Picture(
                            new DocumentFormat.OpenXml.Vml.Shape(new DocumentFormat.OpenXml.Vml.TextBox(box()))))))))));
        }

        var parsed = await ReadAsync(Reader, ms.ToArray(), "boxes.docx");

        Assert.Equal(["Anchor text.", "| Cell text. |"], parsed.Blocks.Select(b => b.Text));
    }

    /// <summary>Builds a DOCX in memory; <paramref name="styles"/> is null for a document with no styles part.</summary>
    private static byte[] Docx(W.Styles? styles, params DocumentFormat.OpenXml.OpenXmlElement[] body)
    {
        using var ms = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(body));
            if (styles is not null) main.AddNewPart<DocumentFormat.OpenXml.Packaging.StyleDefinitionsPart>().Styles = styles;
        }

        return ms.ToArray();
    }

    private static W.Paragraph P(string? style, params DocumentFormat.OpenXml.OpenXmlElement[] content)
    {
        var p = new W.Paragraph(content);
        if (style is not null) p.PrependChild(new W.ParagraphProperties(new W.ParagraphStyleId { Val = style }));
        return p;
    }

    private static W.Run R(string text) => new(new W.Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });

    [Fact] // Final review minor: non-breaking hyphens were dropped (e‑mail → email)
    public async Task Non_breaking_hyphens_are_kept_and_soft_hyphens_ignored()
    {
        var doc = await ReadAsync(Reader, Docx(null, P(null, new W.Run(new W.Text("e"), new W.NoBreakHyphen(), new W.Text("mail and syn"), new W.SoftHyphen(), new W.Text("chronise")))), "h.docx");

        Assert.Equal("e-mail and synchronise", Assert.Single(doc.Blocks).Text);
    }

    [Fact] // Final review minor: numbering from the style with only a level on the paragraph was missed
    public async Task Numbering_from_the_style_applies_when_the_paragraph_sets_only_a_level()
    {
        var styles = new W.Styles(new W.Style(
            new W.StyleName { Val = "List Number" },
            new W.StyleParagraphProperties(new W.NumberingProperties(new W.NumberingId { Val = 3 })))
        { Type = W.StyleValues.Paragraph, StyleId = "ListNumber" });
        var levelOnly = P("ListNumber", R("Second level"));
        levelOnly.ParagraphProperties!.Append(new W.NumberingProperties(new W.NumberingLevelReference { Val = 1 }));

        var doc = await ReadAsync(Reader, Docx(styles, P("ListNumber", R("First level")), levelOnly), "n.docx");

        Assert.Equal("- First level\n  - Second level", Assert.Single(doc.Blocks).Text);
    }

    [Fact] // Final review minor: rows inside a content control in a table were dropped
    public async Task Table_rows_inside_content_controls_are_read()
    {
        static W.TableRow Row(string a) => new(new W.TableCell(P(null, R(a))));
        var table = new W.Table(Row("Plain row"), new W.SdtRow(new W.SdtContentRow(Row("Controlled row"))));

        var doc = await ReadAsync(Reader, Docx(null, table), "t.docx");

        Assert.Equal("| Plain row |\n| Controlled row |", Assert.Single(doc.Blocks).Text);
    }

    [Fact] // Final review minor: table-of-contents entries duplicated headings and added page numbers
    public async Task Table_of_contents_entries_are_skipped()
    {
        var styles = new W.Styles(
            new W.Style(new W.StyleName { Val = "toc 1" }) { Type = W.StyleValues.Paragraph, StyleId = "TOC1" },
            new W.Style(new W.StyleName { Val = "heading 1" }, new W.StyleParagraphProperties(new W.OutlineLevel { Val = 0 })) { Type = W.StyleValues.Paragraph, StyleId = "Heading1" });

        var doc = await ReadAsync(Reader, Docx(styles, P("TOC1", R("Introduction 3")), P("Heading1", R("Introduction")), P(null, R("Body text."))), "toc.docx");

        Assert.Equal(["Introduction", "Body text."], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // PR review: Word's "TOC Heading" ("Contents") was read as an empty heading
    public async Task Table_of_contents_heading_is_skipped()
    {
        var styles = new W.Styles(
            new W.Style(new W.StyleName { Val = "TOC Heading" }, new W.StyleParagraphProperties(new W.OutlineLevel { Val = 0 })) { Type = W.StyleValues.Paragraph, StyleId = "TOCHeading" },
            new W.Style(new W.StyleName { Val = "heading 1" }, new W.StyleParagraphProperties(new W.OutlineLevel { Val = 0 })) { Type = W.StyleValues.Paragraph, StyleId = "Heading1" });

        var doc = await ReadAsync(Reader, Docx(styles, P("TOCHeading", R("Contents")), P("Heading1", R("Introduction")), P(null, R("Body text."))), "toc.docx");

        Assert.Equal(["Introduction", "Body text."], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // PR review: a zip that opens but holds damaged XML was called "unexpected content", not a damaged file
    public async Task Damaged_document_xml_is_reported_as_an_unreadable_file()
    {
        var bytes = Docx(null, P(null, R("Some text that will be cut off.")));
        using var ms = new MemoryStream();
        ms.Write(bytes);
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("word/document.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write(xml[..(xml.Length / 2)]);
        }

        var ex = await Assert.ThrowsAsync<DocumentReadException>(() => ReadAsync(Reader, ms.ToArray(), "cut.docx"));

        Assert.Contains("not a readable DOCX", ex.Reason, StringComparison.Ordinal);
    }

    [Fact] // Final review minor: style names merely containing "code" (Barcode, Postcode) made code blocks
    public async Task Only_code_styles_make_code_blocks()
    {
        var styles = new W.Styles(
            new W.Style(new W.StyleName { Val = "Barcode label" }) { Type = W.StyleValues.Paragraph, StyleId = "BarcodeLabel" },
            new W.Style(new W.StyleName { Val = "Source Code" }) { Type = W.StyleValues.Paragraph, StyleId = "SourceCode" });

        var doc = await ReadAsync(Reader, Docx(styles, P("BarcodeLabel", R("SKU 12345")), P("SourceCode", R("quill sync"))), "c.docx");

        Assert.Equal([BlockKind.Paragraph, BlockKind.Code], doc.Blocks.Select(b => b.Kind));
    }

    [Fact] // Final review minor: a heading style id with no styles part was read as a paragraph
    public async Task Heading_style_ids_work_without_a_styles_part()
    {
        var doc = await ReadAsync(Reader, Docx(null, P("Heading2", R("Setup")), P(null, R("Steps."))), "s.docx");

        Assert.Equal([BlockKind.Heading, BlockKind.Paragraph], doc.Blocks.Select(b => b.Kind));
        Assert.Equal(["Setup"], doc.Blocks[1].HeadingPath);
    }

    [Fact]
    public async Task Embedded_image_gives_one_ImageNotRead_warning()
    {
        var warning = Assert.Single((await Sample()).Warnings);

        Assert.Equal(IngestWarningCodes.ImageNotRead, warning.Code);
        Assert.Equal("1 embedded image(s) were not read; register an OCR provider to read them.", warning.Message);
        Assert.Null(warning.PageNumber);
    }

    [Fact] // Review Focus 2
    public async Task Corrupt_or_truncated_file_throws_DocumentReadException()
    {
        var bytes = await File.ReadAllBytesAsync(SamplePath, Ct);

        var truncated = await Assert.ThrowsAsync<DocumentReadException>(() => ReadAsync(Reader, bytes[..(bytes.Length / 2)], "half.docx"));
        Assert.Equal("half.docx", truncated.FileName);
        Assert.Contains("half.docx", truncated.Message, StringComparison.Ordinal);

        var garbage = new byte[2048];
        new Random(7).NextBytes(garbage);
        await Assert.ThrowsAsync<DocumentReadException>(() => ReadAsync(Reader, garbage, "x.docx"));
    }

    [Theory] // Review Focus 5
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "download", true)]
    [InlineData(null, "Report.DOCX", true)]
    [InlineData(null, "report.doc", false)]
    [InlineData("application/pdf", "x.docx", false)]
    public void CanRead_matches_content_type_first_then_extension(string? contentType, string fileName, bool expected) =>
        Assert.Equal(expected, Reader.CanRead(contentType, fileName));

    [Fact]
    public async Task Pipeline_ingests_docx_after_AddDocxReader()
    {
        var rag = Builder().AddDocxReader().Build();

        var result = await rag.IngestAsync(DocumentSource.FromFile(SamplePath, "guide"), cancellationToken: Ct);

        Assert.Equal(IngestStatus.Created, result.Status);
        Assert.Contains(result.Warnings, w => w.Code == IngestWarningCodes.ImageNotRead);
        var chunks = await rag.RetrieveAsync("Quill works without a connection", cancellationToken: Ct);
        Assert.Contains(chunks, c => c.HeadingPath.SequenceEqual(["Working offline"]));
    }
}
