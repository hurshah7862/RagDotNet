using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Pipeline;
using RagDotNet.Readers.Html;
using static RagDotNet.Readers.Tests.ReaderTestKit;

namespace RagDotNet.Readers.Tests;

public class HtmlReaderTests
{
    private static readonly HtmlReader Reader = new();

    private static Task<ParsedDocument> Sample() => ReadFileAsync(Reader, Asset("html", "sample.html"));

    private static string AllText(ParsedDocument d) => string.Join("\n", d.Blocks.Select(b => b.Text));

    [Fact]
    public async Task Reads_headings_paragraphs_lists_tables_and_code()
    {
        var doc = await Sample();

        Assert.Equal("Quill Help: Sync", doc.Title);
        Assert.Equal(
            [
                BlockKind.Heading, BlockKind.Paragraph,
                BlockKind.Heading, BlockKind.Paragraph, BlockKind.List,
                BlockKind.Heading, BlockKind.Table,
                BlockKind.Heading, BlockKind.Paragraph, BlockKind.Paragraph, BlockKind.Code, BlockKind.Paragraph, BlockKind.List,
            ],
            doc.Blocks.Select(b => b.Kind));

        var heading = doc.Blocks.Single(b => b.Kind == BlockKind.Heading && b.Text == "Sync settings");
        Assert.Equal(["Sync", "How sync works", "Sync settings"], heading.HeadingPath);
        Assert.Equal(["Sync", "Conflicts"], doc.Blocks.Single(b => b.Kind == BlockKind.Code).HeadingPath);

        Assert.Equal(
            "- Notes sync as soon as you stop typing.\n- Attachments sync in the background:\n  - images first,\n  - then other files.\n- Settings sync once a minute.",
            doc.Blocks.First(b => b.Kind == BlockKind.List).Text);
        Assert.Equal(
            "| Setting | Default |\n| Sync on mobile data | Off |\n| Largest attachment to sync | 200 MB |",
            doc.Blocks.Single(b => b.Kind == BlockKind.Table).Text);
        Assert.Equal("is:conflict\nsort:modified", doc.Blocks.Single(b => b.Kind == BlockKind.Code).Text);
        Assert.EndsWith("slow connection.\nAttachments are uploaded separately, in the background.", doc.Blocks[3].Text, StringComparison.Ordinal);
        Assert.Equal(
            "- Local version: The note as it is on this device.\n- Remote version: The note as it arrived from another device.",
            doc.Blocks[^1].Text);
    }

    [Fact] // Review Focus 3
    public async Task Boilerplate_and_hidden_text_never_reach_blocks()
    {
        var text = AllText(await Sample());

        foreach (var marker in new[] { "SECRET_SCRIPT", "STYLE_TEXT", "HIDDEN_TEXT", "DISPLAY_NONE_TEXT", "NAV_LINK", "HEADER_TEXT", "COOKIE_TEXT", "ASIDE_TEXT", "FOOTER_TEXT", "SKIP_TEXT" })
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_main_the_single_article_is_the_root()
    {
        var doc = await ReadAsync(Reader, """
            <html><body>
              <div class="menu">MENU_TEXT outside the article</div>
              <article><header><h1>Release notes</h1></header><p>Version 4 adds templates.</p><footer>Posted in May</footer></article>
            </body></html>
            """, "a.html");

        Assert.DoesNotContain("MENU_TEXT", AllText(doc), StringComparison.Ordinal);
        Assert.Equal(["Release notes", "Version 4 adds templates.", "Posted in May"], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // Review Focus 3
    public async Task Without_main_or_article_the_body_is_used_minus_boilerplate()
    {
        var doc = await ReadAsync(Reader, """
            <html><body>
              <nav>NAV_TEXT</nav>
              <div class="cookie-banner">COOKIE_TEXT accept</div>
              <h2>Shortcuts</h2>
              <div>Press Ctrl+K to open the command palette.</div>
              <footer>FOOTER_TEXT</footer>
              <script>var x = "SCRIPT_TEXT";</script>
            </body></html>
            """, "b.html");

        Assert.Equal(["Shortcuts", "Press Ctrl+K to open the command palette."], doc.Blocks.Select(b => b.Text));
        Assert.Equal(["Shortcuts"], doc.Blocks[1].HeadingPath);
    }

    [Fact]
    public async Task Data_uri_images_give_one_ImageNotRead_warning_and_remote_images_none()
    {
        var doc = await Sample();

        var warning = Assert.Single(doc.Warnings);
        Assert.Equal(IngestWarningCodes.ImageNotRead, warning.Code);
        Assert.Equal("1 embedded image(s) were not read; register an OCR provider to read them.", warning.Message);
        Assert.Null(warning.PageNumber);

        var remoteOnly = await ReadAsync(Reader, "<p>Text</p><img src=\"https://example.com/a.png\">", "c.html");
        Assert.Empty(remoteOnly.Warnings);
    }

    [Fact] // Review Focus 2 (HTML case)
    public async Task Damaged_or_empty_input_never_throws()
    {
        byte[] damaged = [.. "<html><head><meta charset=\"utf-8\"></head><body><p>Good text "u8, 0xC3, 0x28, 0xFF, .. " still here.</p></body></html>"u8];
        var doc = await ReadAsync(Reader, damaged, "d.html");
        var paragraph = Assert.Single(doc.Blocks);
        Assert.StartsWith("Good text ", paragraph.Text, StringComparison.Ordinal);
        Assert.EndsWith(" still here.", paragraph.Text, StringComparison.Ordinal);

        Assert.Empty((await ReadAsync(Reader, [], "e.html")).Blocks);
    }

    [Theory] // Review Focus 5
    [InlineData("text/html; charset=utf-8", "x.bin", true)]
    [InlineData("application/xhtml+xml", "x", true)]
    [InlineData(null, "Page.HTM", true)]
    [InlineData(null, "page.xhtml", true)]
    [InlineData("text/plain", "x.html", false)]
    [InlineData(null, "x.md", false)]
    public void CanRead_matches_content_type_first_then_extension(string? contentType, string fileName, bool expected) =>
        Assert.Equal(expected, Reader.CanRead(contentType, fileName));

    [Fact]
    public async Task Pipeline_ingests_html_after_AddHtmlReader()
    {
        var rag = Builder().AddHtmlReader().Build();
        await AssertIngestsSample(rag);

        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashEmbedder());
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddRag(o => { o.CollectionName = "readers"; o.EmbeddingDimensions = HashEmbedder.Dimensions; }).AddHtmlReader();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await AssertIngestsSample(scope.ServiceProvider.GetRequiredService<IRagPipeline>());
    }

    private static async Task AssertIngestsSample(IRagPipeline rag)
    {
        var result = await rag.IngestAsync(DocumentSource.FromFile(Asset("html", "sample.html"), "sync"), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Created, result.Status);

        var chunks = await rag.RetrieveAsync("conflict both versions choose which one to keep", cancellationToken: Ct);
        Assert.Contains(chunks, c => c.HeadingPath.SequenceEqual(["Sync", "Conflicts"]));
    }
}
