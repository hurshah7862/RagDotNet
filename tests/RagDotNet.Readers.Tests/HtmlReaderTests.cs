using RagDotNet.Configuration;
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

    [Theory] // Final review, critical: substring class/id matching deleted real content
    [InlineData("<main><h1>Prices</h1><table class='share-price'><tr><td>REAL_TABLE</td></tr></table><p class='shareholder-note'>REAL_P</p></main>")]
    [InlineData("<body><div class='shared-notes'><p>REAL_P</p></div><div id='sidebar-toggle-help'><p>REAL_TABLE</p></div></body>")]
    [InlineData("<body><div class='site-content has-sidebar'><main><p>REAL_P</p><p>REAL_TABLE</p></main></div></body>")]
    [InlineData("<body><div class='layout no-sidebar'><article class='recipe cookie-recipes'><p>REAL_P</p><p>REAL_TABLE</p></article></div></body>")]
    [InlineData("<body><div role='main' class='content with-sidebar'><div class='gdpr-guide-article'><p>REAL_P</p></div><div class='consentful-design'><p>REAL_TABLE</p></div></div></body>")]
    public async Task Class_and_id_words_inside_real_content_never_remove_it(string html)
    {
        var text = AllText(await ReadAsync(Reader, html, "page.html"));

        Assert.Contains("REAL_P", text, StringComparison.Ordinal);
        Assert.Contains("REAL_TABLE", text, StringComparison.Ordinal);
    }

    [Fact] // Final review, critical: exact boilerplate names are still removed
    public async Task Exact_boilerplate_class_names_are_still_removed()
    {
        var doc = await ReadAsync(Reader, """
            <body>
              <div class="cookie-banner">COOKIE_TEXT</div><div id="sidebar">SIDEBAR_TEXT</div>
              <div class="social share-buttons">SHARE_TEXT</div><ol class="breadcrumbs"><li>CRUMB_TEXT</li></ol>
              <p>Real paragraph.</p>
            </body>
            """, "x.html");

        Assert.Equal(["Real paragraph."], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // Final review: ASP.NET WebForms pages wrap the whole body in one form
    public async Task A_form_wrapping_the_page_keeps_its_content_but_drops_controls()
    {
        var doc = await ReadAsync(Reader, """
            <body><form id="form1" method="post" action="./policy.aspx">
              <input type="hidden" name="__VIEWSTATE" value="VIEWSTATE_TEXT">
              <div class="page"><h1>Intranet policy</h1><p>Laptops must be encrypted.</p></div>
              <label>LABEL_TEXT</label><select><option>OPTION_TEXT</option></select><button>BUTTON_TEXT</button>
            </form></body>
            """, "policy.aspx.html");

        Assert.Equal(["Intranet policy", "Laptops must be encrypted."], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // Final review: a search form outside the content is still removed
    public async Task A_small_form_outside_main_is_removed()
    {
        var doc = await ReadAsync(Reader, "<body><form class='search'><p>SEARCH_HINT</p><input name='q'></form><main><p>Body text.</p></main></body>", "x.html");

        Assert.Equal(["Body text."], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // PR review: on a body root, a search form with one hint paragraph leaked into the text
    public async Task A_small_form_on_a_body_root_is_removed_even_with_a_hint_paragraph()
    {
        var doc = await ReadAsync(Reader, "<body><form class='search'><p>SEARCH_HINT</p><input name='q'></form><h1>Page</h1><p>Body text.</p></body>", "x.html");

        Assert.Equal(["Page", "Body text."], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // PR review: page-level rules on a body root had no test of their own
    public async Task On_a_body_root_asides_and_page_headers_go_but_section_headers_and_until_found_stay()
    {
        var doc = await ReadAsync(Reader, """
            <body><header>SITE_HEADER</header><aside>ASIDE_TEXT</aside>
              <section><header><h2>Part one</h2></header><p>Details.</p></section>
              <div hidden="until-found"><p>Findable text.</p></div>
              <footer>SITE_FOOTER</footer></body>
            """, "x.html");

        Assert.Equal(["Part one", "Details.", "Findable text."], doc.Blocks.Select(b => b.Text));
    }

    [Theory] // PR review: an article in a sidebar, nav or hidden element became the content root and the page was lost
    [InlineData("<aside><article>TEASER</article></aside>")]
    [InlineData("<nav><article>TEASER</article></nav>")]
    [InlineData("<div hidden><article>TEASER</article></div>")]
    [InlineData("<div class='sidebar'><article>TEASER</article></div>")]
    public async Task An_article_inside_page_furniture_is_never_the_content_root(string furniture)
    {
        var doc = await ReadAsync(Reader, $"<body><div id='content'><h1>Real page</h1><p>Real text.</p></div>{furniture}</body>", "x.html");

        Assert.Equal(["Real page", "Real text."], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // PR review: a hidden main (an inactive view) became the root ahead of the visible one
    public async Task A_hidden_main_is_skipped_for_the_visible_one()
    {
        var doc = await ReadAsync(Reader, "<body><main hidden><p>OLD_VIEW</p></main><main><p>Visible text.</p></main></body>", "x.html");

        Assert.Equal(["Visible text."], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // PR review: class names separated by tabs or newlines were not matched
    public async Task Boilerplate_class_names_match_across_any_whitespace()
    {
        var doc = await ReadAsync(Reader, "<main><div class=\"wide\tsidebar\">SIDEBAR_TEXT</div><div class=\"x\n  cookie-banner\">COOKIE_TEXT</div><p>Body.</p></main>", "x.html");

        Assert.Equal(["Body."], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // PR review: the title fallback took the first heading, not the highest one the docs promise
    public async Task Without_a_title_element_the_highest_heading_is_the_title()
    {
        var doc = await ReadAsync(Reader, "<body><h2>Tagline</h2><h1>Real title</h1><p>Text.</p></body>", "x.html");

        Assert.Equal("Real title", doc.Title);
    }

    [Fact] // Final review: header inside main or section holds the page's heading
    public async Task Headers_inside_the_content_keep_their_headings()
    {
        var doc = await ReadAsync(Reader, """
            <body><header>SITE_HEADER</header><main>
              <header><h1>Post title</h1></header><p>Intro.</p>
              <section><header><h2>Part one</h2></header><p>Details.</p></section>
            </main></body>
            """, "x.html");

        Assert.Equal(["Post title", "Intro.", "Part one", "Details."], doc.Blocks.Select(b => b.Text));
        Assert.Equal(["Post title", "Part one"], doc.Blocks[3].HeadingPath);
    }

    [Fact] // Final review: block content inside an inline wrapper (a card link) was flattened
    public async Task Blocks_inside_inline_wrappers_stay_blocks()
    {
        var doc = await ReadAsync(Reader, "<main><a href='/card'><div><h2>Card title</h2><p>Card body text.</p></div></a></main>", "x.html");

        Assert.Equal([BlockKind.Heading, BlockKind.Paragraph], doc.Blocks.Select(b => b.Kind));
        Assert.Equal(["Card title"], doc.Blocks[1].HeadingPath);
    }

    [Fact] // Final review: valid HTML groups dt/dd in div, and nests lists inside divs in li
    public async Task Definition_groups_and_lists_nested_in_divs_are_read()
    {
        var doc = await ReadAsync(Reader, """
            <main>
              <dl><div><dt>Term</dt><dd>Definition text.</dd></div></dl>
              <ul><li><div>Item</div><div><ul><li>Sub item</li></ul></div></li></ul>
            </main>
            """, "x.html");

        Assert.Equal(["- Term: Definition text.", "- Item\n  - Sub item"], doc.Blocks.Select(b => b.Text));
    }

    [Fact] // Final review minor: colspan shifted the cells of a row
    public async Task Colspan_keeps_columns_aligned()
    {
        var doc = await ReadAsync(Reader, "<table><tr><th colspan='2'>Plan</th><th>Price</th></tr><tr><td>Team</td><td>Yearly</td><td>96</td></tr></table>", "t.html");

        Assert.Equal("| Plan |  | Price |\n| Team | Yearly | 96 |", Assert.Single(doc.Blocks).Text);
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
