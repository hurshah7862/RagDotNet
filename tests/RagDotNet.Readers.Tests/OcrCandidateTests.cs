using RagDotNet.Documents;
using RagDotNet.Readers.Docx;
using RagDotNet.Readers.Html;
using RagDotNet.Readers.Pdf;
using RagDotNet.TestAssetGen;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static RagDotNet.Readers.Tests.ReaderTestKit;

namespace RagDotNet.Readers.Tests;

public class OcrCandidateTests
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    [Fact]
    public async Task Pdf_scanned_page_is_a_ScannedPage_candidate_at_its_page_position()
    {
        var doc = await ReadFileAsync(new PdfReader(), Asset("pdf", "sample.pdf"));

        var page = Assert.Single(doc.OcrCandidates, c => c.Kind == OcrCandidateKind.ScannedPage);
        Assert.Equal(2, page.PageNumber);
        Assert.False(page.HasTextLayer);
        Assert.Null(page.Image);
        Assert.Equal(doc.Blocks.ToList().FindIndex(b => b.PageNumber == 3), page.InsertAt);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public async Task Pdf_image_on_a_text_page_is_placed_between_the_paragraphs_around_it()
    {
        var pdf = OnePage(p =>
        {
            p.Text("Above the image, a paragraph of ordinary text.", 760);
            p.Image(Png.Solid(40, 20, 0x80), new PdfRectangle(72, 600, 272, 700));
            p.Text("Below the image, another paragraph of text.", 500);
        });

        var doc = await ReadAsync(new PdfReader(), pdf, "between.pdf");

        Assert.Equal(["Above the image, a paragraph of ordinary text.", "Below the image, another paragraph of text."], doc.Blocks.Select(b => b.Text));
        var image = Assert.Single(doc.OcrCandidates);
        Assert.Equal(OcrCandidateKind.Image, image.Kind);
        Assert.Equal(1, image.PageNumber);
        Assert.Equal(1, image.InsertAt);
        Assert.False(image.IsPageBackground);
        Assert.Equal("image/png", image.Image!.MediaType);
        Assert.Equal(PngSignature, image.Image.Bytes.Span[..4].ToArray());
        Assert.Equal((40, 20), (image.Image.Width, image.Image.Height));
    }

    [Fact]
    public async Task Pdf_full_page_image_under_text_is_a_page_background()
    {
        var pdf = OnePage(p =>
        {
            p.Image(Png.Solid(60, 80, 0xF0), new PdfRectangle(0, 0, 595, 842));
            p.Text("A searchable scan: this text layer sits over the page image.", 760);
        });

        var doc = await ReadAsync(new PdfReader(), pdf, "searchable.pdf");

        var image = Assert.Single(doc.OcrCandidates);
        Assert.Equal(OcrCandidateKind.Image, image.Kind);
        Assert.True(image.IsPageBackground);
    }

    [Fact]
    public async Task Pdf_stamp_over_a_scan_is_also_a_ScannedPage_with_a_text_layer()
    {
        var pdf = OnePage(p =>
        {
            p.Image(Png.Solid(60, 80, 0xF0), new PdfRectangle(0, 0, 595, 842));
            p.Text("ABC-000123", 40);
        });

        var doc = await ReadAsync(new PdfReader(), pdf, "stamp.pdf");

        var page = Assert.Single(doc.OcrCandidates, c => c.Kind == OcrCandidateKind.ScannedPage);
        Assert.True(page.HasTextLayer);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(doc.Blocks.Count, page.InsertAt);
        Assert.True(Assert.Single(doc.OcrCandidates, c => c.Kind == OcrCandidateKind.Image).IsPageBackground);
    }

    [Fact]
    public async Task Docx_image_is_a_candidate_after_its_paragraph_with_part_bytes_and_extent_size()
    {
        var doc = await ReadFileAsync(new DocxReader(), Asset("docx", "sample.docx"));

        var image = Assert.Single(doc.OcrCandidates);
        Assert.Equal(OcrCandidateKind.Image, image.Kind);
        Assert.Null(image.PageNumber);
        Assert.Equal(BlockKind.Code, doc.Blocks[image.InsertAt - 1].Kind);
        Assert.Equal("Changes upload when you reconnect.", doc.Blocks[image.InsertAt].Text);
        Assert.Equal("image/png", image.Image!.MediaType);
        Assert.Equal(PngSignature, image.Image.Bytes.Span[..4].ToArray());
        Assert.Equal((40, 20), (image.Image.Width, image.Image.Height));
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public async Task Html_data_uri_image_is_a_candidate_and_remote_images_are_not()
    {
        var doc = await ReadFileAsync(new HtmlReader(), Asset("html", "sample.html"));

        var image = Assert.Single(doc.OcrCandidates);
        Assert.Equal(OcrCandidateKind.Image, image.Kind);
        Assert.Null(image.PageNumber);
        Assert.StartsWith("The banner shown", doc.Blocks[image.InsertAt].Text, StringComparison.Ordinal);
        Assert.Equal("image/png", image.Image!.MediaType);
        Assert.Equal(PngSignature, image.Image.Bytes.Span[..4].ToArray());
        Assert.Empty(doc.Warnings);

        var remoteOnly = await ReadAsync(new HtmlReader(), "<p>Text</p><img src=\"https://example.com/a.png\">", "c.html");
        Assert.Empty(remoteOnly.OcrCandidates);
    }

    [Fact]
    public async Task Html_images_inside_paragraphs_and_lists_are_candidates_after_their_block()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var html = $"<main><p>First <img src=\"data:image/png;base64,{png}\" width=\"200\" height=\"100\"> text.</p><ul><li>Item <img src=\"data:image/png;base64,{png}\"></li></ul><p>Last.</p></main>";

        var doc = await ReadAsync(new HtmlReader(), html, "inline.html");

        Assert.Equal([1, 2], doc.OcrCandidates.Select(c => c.InsertAt));
        Assert.Equal((200, 100), (doc.OcrCandidates[0].Image!.Width, doc.OcrCandidates[0].Image!.Height));
    }

    [Theory] // Final review Important 2: images were decoded even when nothing would read them
    [InlineData("pdf", "sample.pdf")]
    [InlineData("docx", "sample.docx")]
    [InlineData("html", "sample.html")]
    public async Task Without_ExtractImages_images_are_candidates_without_bytes(string folder, string file)
    {
        IDocumentReader reader = folder switch { "pdf" => new PdfReader(), "docx" => new DocxReader(), _ => new HtmlReader() };
        using var stream = File.OpenRead(Asset(folder, file));

        var doc = await reader.ReadAsync(stream, new DocumentReadContext(file, null) { ExtractImages = false }, Ct);

        var images = doc.OcrCandidates.Where(c => c.Kind == OcrCandidateKind.Image).ToList();
        Assert.NotEmpty(images);
        Assert.All(images, c => Assert.Null(c.Image));
    }

    [Fact] // Final review minor 9: an image in loose text was placed before its own paragraph
    public async Task Html_image_in_loose_text_goes_after_that_text()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var html = $"<main><div>Intro text <img src=\"data:image/png;base64,{png}\"> more text.</div><p>After.</p></main>";

        var doc = await ReadAsync(new HtmlReader(), html, "loose.html");

        Assert.Equal(["Intro text more text.", "After."], doc.Blocks.Select(b => b.Text));
        Assert.Equal(1, Assert.Single(doc.OcrCandidates).InsertAt);
    }

    private static byte[] OnePage(Action<PageWriter> draw)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        draw(new PageWriter(page, font));
        return builder.Build();
    }

    private sealed class PageWriter(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font)
    {
        public void Text(string text, double y) => page.AddText(text, 11, new PdfPoint(72, y), font);

        public void Image(byte[] png, PdfRectangle where) => page.AddPng(png, where);
    }
}
