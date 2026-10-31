using System.Text;
using RagDotNet.Documents;
using RagDotNet.Readers;

namespace RagDotNet.Core.Tests;

public class ReaderTests
{
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static MemoryStream Utf8(string s) => new(Encoding.UTF8.GetBytes(s));

    private static Task<ParsedDocument> ReadText(Stream s) =>
        new PlainTextReader().ReadAsync(s, new DocumentReadContext("a.txt", "text/plain"), TestContext.Current.CancellationToken);

    private static Task<ParsedDocument> ReadMd(string md) =>
        new MarkdownReader().ReadAsync(Utf8(md), new DocumentReadContext("a.md", "text/markdown"), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Text_reader_splits_paragraphs_on_blank_lines()
    {
        var doc = await ReadText(Utf8("a\nb\n\nc"));
        Assert.Equal(2, doc.Blocks.Count);
        Assert.All(doc.Blocks, b =>
        {
            Assert.Equal(BlockKind.Paragraph, b.Kind);
            Assert.Empty(b.HeadingPath);
        });
        Assert.Equal("a\nb", doc.Blocks[0].Text);
        Assert.Equal("c", doc.Blocks[1].Text);
    }

    [Fact]
    public async Task Text_reader_decodes_utf8_bom_utf16_bom_and_invalid_bytes()
    {
        var plain = await ReadText(File.OpenRead(Path.Combine(Assets, "text", "plain.txt")));
        var utf16 = await ReadText(File.OpenRead(Path.Combine(Assets, "text", "utf16-bom.txt")));
        Assert.Equal(plain.Blocks.Select(b => b.Text), utf16.Blocks.Select(b => b.Text));
        Assert.NotEmpty(utf16.Blocks);

        var bom = await ReadText(new MemoryStream([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']));
        Assert.Equal("hi", Assert.Single(bom.Blocks).Text);

        var bad = await ReadText(new MemoryStream([(byte)'a', 0xFF, (byte)'b']));
        Assert.Equal("a�b", Assert.Single(bad.Blocks).Text);
    }

    [Fact]
    public async Task Markdown_reader_tracks_heading_path()
    {
        var doc = await ReadMd("# Install\nintro\n## Linux\napt\n## Windows\nwinget\n# Use\nrun");
        Assert.Equal(["Install", "Linux"], doc.Blocks.Single(b => b.Text == "apt").HeadingPath);
        Assert.Equal(["Install", "Windows"], doc.Blocks.Single(b => b.Text == "winget").HeadingPath);
        Assert.Equal(["Use"], doc.Blocks.Single(b => b.Text == "run").HeadingPath);
        var heading = doc.Blocks.First(b => b.Kind == BlockKind.Heading && b.Text.Contains("Linux"));
        Assert.Equal(["Install", "Linux"], heading.HeadingPath);
    }

    [Fact]
    public async Task Markdown_reader_emits_code_table_and_list_blocks_with_source_text()
    {
        var code = "```csharp\nvar x = 1;\n```";
        var table = "| a | b |\n|---|---|\n| 1 | 2 |";
        var list = "- a\n- b";
        var doc = await ReadMd($"{code}\n\n{table}\n\n{list}\n");
        Assert.Equal(BlockKind.Code, doc.Blocks[0].Kind);
        Assert.Equal(code, doc.Blocks[0].Text);
        Assert.Equal(BlockKind.Table, doc.Blocks[1].Kind);
        Assert.Equal(table, doc.Blocks[1].Text);
        Assert.Equal(BlockKind.List, doc.Blocks[2].Kind);
        Assert.Equal(list, doc.Blocks[2].Text);
    }

    [Fact]
    public async Task Markdown_reader_uses_first_h1_as_title()
    {
        var doc = await ReadMd("intro\n\n# First\n\n# Second\n");
        Assert.Equal("First", doc.Title);
    }

    [Fact]
    public async Task Markdown_reader_reads_install_guide_asset()
    {
        var doc = await new MarkdownReader().ReadAsync(
            File.OpenRead(Path.Combine(Assets, "markdown", "install-guide.md")),
            new DocumentReadContext("install-guide.md", null), TestContext.Current.CancellationToken);
        Assert.Equal("Install", doc.Title);
        Assert.Contains(doc.Blocks, b => b.HeadingPath.SequenceEqual(["Install", "Linux"]) && b.Text.Contains("apt install"));
        Assert.Contains(doc.Blocks, b => b.HeadingPath.SequenceEqual(["Install", "Windows"]) && b.Text.Contains("winget install"));
    }

    [Fact]
    public void Document_source_rejects_blank_id()
    {
        Assert.Throws<ArgumentException>(() => new DocumentSource(" ", new MemoryStream(), "a.txt"));
        Assert.Throws<ArgumentException>(() => new DocumentSource("", new MemoryStream(), "a.txt"));
    }

    [Fact]
    public void Selector_throws_unsupported_for_unknown_format()
    {
        var sel = new ReaderSelector([new PlainTextReader(), new MarkdownReader()]);
        Assert.Throws<UnsupportedDocumentException>(() => sel.Select(null, "x.xyz"));
    }

    [Fact]
    public void Selector_prefers_content_type_then_extension()
    {
        var sel = new ReaderSelector([new PlainTextReader(), new MarkdownReader()]);
        Assert.IsType<MarkdownReader>(sel.Select("text/markdown", "notes.txt"));
        Assert.IsType<MarkdownReader>(sel.Select(null, "notes.md"));
        Assert.IsType<PlainTextReader>(sel.Select(null, "notes.TXT"));
    }
}
