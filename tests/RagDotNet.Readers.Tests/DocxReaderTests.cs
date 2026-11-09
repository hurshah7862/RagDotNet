using Microsoft.Extensions.DependencyInjection;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Readers.Docx;
using static RagDotNet.Readers.Tests.ReaderTestKit;

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
