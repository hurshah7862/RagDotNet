using System.Text;
using Microsoft.Extensions.DataIngestion;
using RagDotNet.Documents;

namespace RagDotNet.DataIngestion.Tests;

public sealed class DataIngestionReaderTests
{
    internal const string Handbook = """
        # Handbook

        Intro paragraph about the handbook.

        ## Leave

        You get 25 days of leave. Ask your manager first.

        | Col A | Col B |
        |---|---|
        | 1 | 2 |

        ### Sick leave

        Tell HR on the first day.

        ## Expenses

        Keep receipts.
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DocumentReadContext Context = new("doc.md", "text/markdown");

    private static Task<ParsedDocument> ReadMarkdownAsync(string markdown, DocumentReadContext? context = null) =>
        new DataIngestionReader(new MarkdownReader(), [".md"])
            .ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(markdown)), context ?? Context, Ct);

    private static Task<ParsedDocument> ReadFakeAsync(FakeReader reader, DocumentReadContext? context = null) =>
        new DataIngestionReader(reader, [".bin"]).ReadAsync(new MemoryStream([1]), context ?? Context, Ct);

    private static FakeReader Elements(params IngestionDocumentElement[] elements)
    {
        var section = new IngestionDocumentSection();
        foreach (var e in elements) section.Elements.Add(e);
        var document = new IngestionDocument("doc");
        document.Sections.Add(section);
        return new FakeReader(_ => document);
    }

    [Theory]
    [InlineData("text/markdown; charset=utf-8", "a.txt", true)]
    [InlineData(null, "notes.MD", true)]
    [InlineData(null, "notes.txt", false)]
    [InlineData("text/plain", "notes.txt", false)]
    public void CanRead_matches_extension_or_content_type(string? contentType, string fileName, bool expected) =>
        Assert.Equal(expected, new DataIngestionReader(new MarkdownReader(), [".md"], ["text/markdown"]).CanRead(contentType, fileName));

    [Fact]
    public void Constructor_requires_an_extension_or_content_type() =>
        Assert.Throws<ArgumentException>(() => new DataIngestionReader(new MarkdownReader(), [], []));

    [Fact]
    public async Task Headings_build_the_path_and_title()
    {
        var parsed = await ReadMarkdownAsync(Handbook);

        Assert.Equal("Handbook", parsed.Title);
        Assert.Equal(["Handbook", "Leave", "Sick leave"], parsed.Blocks.Single(b => b.Text.Contains("Tell HR")).HeadingPath);
        var expenses = parsed.Blocks.Single(b => b.Kind == BlockKind.Heading && b.Text == "## Expenses");
        Assert.Equal(["Handbook", "Expenses"], expenses.HeadingPath);
    }

    [Fact]
    public async Task Heading_text_with_hashes_is_kept()
    {
        var parsed = await ReadMarkdownAsync("# C# ## guide\n\nBody.\n");

        Assert.Equal(["C# ## guide"], parsed.Blocks.Single(b => b.Text == "Body.").HeadingPath);
    }

    [Fact]
    public async Task Tables_are_table_blocks()
    {
        var parsed = await ReadMarkdownAsync(Handbook);

        var table = Assert.Single(parsed.Blocks, b => b.Kind == BlockKind.Table);
        Assert.Contains("| 1 | 2 |", table.Text);
    }

    [Fact]
    public async Task Inline_image_keeps_paragraph_text()
    {
        var parsed = await ReadMarkdownAsync("Body ![alt text](img.png) more.\n");

        var block = Assert.Single(parsed.Blocks);
        Assert.Contains("Body", block.Text);
        Assert.Contains("more.", block.Text);
    }

    [Fact]
    public async Task Image_with_bytes_becomes_ocr_candidate_and_alt_text()
    {
        var image = new IngestionDocumentImage("![chart](data:image/png;base64,AQID)")
        {
            Content = new byte[] { 1, 2, 3 },
            MediaType = "image/png",
            AlternativeText = "chart",
            PageNumber = 4,
        };

        var parsed = await ReadFakeAsync(Elements(image));

        var candidate = Assert.Single(parsed.OcrCandidates);
        Assert.Equal(OcrCandidateKind.Image, candidate.Kind);
        Assert.Equal(0, candidate.InsertAt);
        Assert.Equal(4, candidate.PageNumber);
        Assert.Equal("image/png", candidate.Image!.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, candidate.Image.Bytes.ToArray());
        var block = Assert.Single(parsed.Blocks);
        Assert.Equal((BlockKind.Paragraph, "chart"), (block.Kind, block.Text));
    }

    [Fact]
    public async Task ExtractImages_off_gives_no_candidates()
    {
        var image = new IngestionDocumentImage("![chart](x)") { Content = new byte[] { 1 }, AlternativeText = "chart" };

        var parsed = await ReadFakeAsync(Elements(image), Context with { ExtractImages = false });

        Assert.Empty(parsed.OcrCandidates);
    }

    [Fact]
    public async Task Page_numbers_are_kept()
    {
        var parsed = await ReadFakeAsync(Elements(
            new IngestionDocumentParagraph("one") { PageNumber = 1 },
            new IngestionDocumentParagraph("two") { PageNumber = 2 }));

        Assert.Equal([1, 2], parsed.Blocks.Select(b => b.PageNumber));
    }

    [Fact]
    public async Task Null_header_level_counts_as_level_one()
    {
        var parsed = await ReadFakeAsync(Elements(new IngestionDocumentHeader("Heading"), new IngestionDocumentParagraph("body")));

        Assert.Equal(["Heading"], parsed.Blocks.Single(b => b.Text == "body").HeadingPath);
        Assert.Equal("Heading", parsed.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n\n ")]
    public async Task Empty_and_whitespace_input_give_an_empty_document(string markdown)
    {
        var parsed = await ReadMarkdownAsync(markdown);

        Assert.Empty(parsed.Blocks);
    }

    [Fact]
    public async Task Reader_errors_become_DocumentReadException()
    {
        var e = await Assert.ThrowsAsync<DocumentReadException>(() => ReadFakeAsync(new FakeReader(_ => throw new InvalidDataException("bad"))));

        Assert.Contains("bad", e.Message);
        Assert.IsType<InvalidDataException>(e.InnerException);
    }

    [Fact]
    public async Task Cancellation_passes_through() =>
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReadFakeAsync(new FakeReader(_ => throw new OperationCanceledException())));

    internal sealed class FakeReader(Func<Stream, IngestionDocument> read) : IngestionDocumentReader
    {
        public override Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken) =>
            Task.FromResult(read(source));
    }
}
