using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Readers.Pdf;
using static RagDotNet.Readers.Tests.ReaderTestKit;

namespace RagDotNet.Readers.Tests;

public class PdfReaderTests
{
    private static readonly PdfReader Reader = new();

    private static string SamplePath => Asset("pdf", "sample.pdf");

    private static Task<ParsedDocument> Sample() => ReadFileAsync(Reader, SamplePath);

    [Fact]
    public async Task Blocks_carry_their_page_numbers()
    {
        var doc = await Sample();

        Assert.All(doc.Blocks, b => Assert.NotNull(b.PageNumber));
        Assert.Equal([1, 3], doc.Blocks.Select(b => b.PageNumber!.Value).Distinct().Order());
        Assert.Contains(doc.Blocks, b => b.PageNumber == 1 && b.Text.Contains("share a notebook with up to fifty people", StringComparison.Ordinal));
        Assert.Contains(doc.Blocks, b => b.PageNumber == 3 && b.Text.Contains("cached for thirty days", StringComparison.Ordinal));
    }

    [Fact] // Review Focus 1
    public async Task Scanned_page_gives_a_PageWithoutText_warning_and_other_pages_are_read()
    {
        var doc = await Sample();

        var warning = Assert.Single(doc.Warnings, w => w.Code == IngestWarningCodes.PageWithoutText);
        Assert.Equal(2, warning.PageNumber);
        Assert.Equal("Page 2 has no extractable text; register an OCR provider to read it.", warning.Message);
        Assert.DoesNotContain(doc.Blocks, b => b.PageNumber == 2);
    }

    [Fact]
    public async Task Image_on_a_text_page_gives_an_ImageNotRead_warning_with_its_page()
    {
        var warning = Assert.Single((await Sample()).Warnings, w => w.Code == IngestWarningCodes.ImageNotRead);

        Assert.Equal(3, warning.PageNumber);
        Assert.Equal("Page 3 has 1 image(s) that were not read; register an OCR provider to read them.", warning.Message);
    }

    [Fact]
    public async Task Larger_fonts_become_headings_ranked_by_size()
    {
        var doc = await Sample();
        var headings = doc.Blocks.Where(b => b.Kind == BlockKind.Heading).ToList();

        Assert.Equal(["Quill for Teams", "Sharing a notebook", "Offline access"], headings.Select(h => h.Text));
        Assert.Equal(["Quill for Teams"], headings[0].HeadingPath);
        Assert.Equal(["Quill for Teams", "Sharing a notebook"], headings[1].HeadingPath);
        Assert.Equal(["Quill for Teams", "Offline access"], headings[2].HeadingPath);
        Assert.Equal("Quill for Teams", doc.Title);
    }

    [Fact]
    public async Task Courier_text_becomes_a_code_block_with_line_breaks()
    {
        var code = Assert.Single((await Sample()).Blocks, b => b.Kind == BlockKind.Code);

        Assert.Equal("quill share --notebook Recipes\nquill members --notebook Recipes", code.Text);
        Assert.Equal(["Quill for Teams", "Sharing a notebook"], code.HeadingPath);
    }

    [Fact]
    public async Task Bullet_lines_become_a_list_block()
    {
        var list = Assert.Single((await Sample()).Blocks, b => b.Kind == BlockKind.List);

        Assert.Equal("- Owners can invite and remove members.\n- Editors can change any note.\n- Viewers can read notes but not change them.", list.Text);
    }

    [Fact]
    public async Task Ingest_through_the_pipeline_reports_the_page_warning_and_cites_pages()
    {
        var rag = Builder().UseChatClient(new CiteFirstChat()).AddPdfReader().Build();

        var result = await rag.IngestAsync(DocumentSource.FromFile(SamplePath, "teams"), cancellationToken: Ct);

        Assert.Equal(IngestStatus.Created, result.Status);
        Assert.Contains(result.Warnings, w => w.Code == IngestWarningCodes.PageWithoutText && w.PageNumber == 2);

        var answer = await rag.AskAsync("How long are notes cached offline for thirty days?", cancellationToken: Ct);
        var citation = Assert.Single(answer.Citations);
        Assert.NotNull(citation.Chunk.PageStart);
        Assert.Contains("p.", citation.Location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Review Focus 2
    public async Task Corrupt_or_truncated_pdf_throws_DocumentReadException()
    {
        var bytes = await File.ReadAllBytesAsync(SamplePath, Ct);

        var truncated = await Assert.ThrowsAsync<DocumentReadException>(() => ReadAsync(Reader, bytes[..(bytes.Length / 3)], "third.pdf"));
        Assert.Equal("third.pdf", truncated.FileName);

        var garbage = new byte[4096];
        new Random(11).NextBytes(garbage);
        var ex = await Assert.ThrowsAsync<DocumentReadException>(() => ReadAsync(Reader, garbage, "x.pdf"));
        Assert.Contains("x.pdf", ex.Message, StringComparison.Ordinal);
    }

    [Theory] // Review Focus 5
    [InlineData(null, "Report.PDF", true)]
    [InlineData("application/pdf", "download", true)]
    [InlineData("application/pdf; name=x", "x", true)]
    [InlineData("text/plain", "x.pdf", false)]
    [InlineData(null, "x.docx", false)]
    public void CanRead_matches_content_type_first_then_extension(string? contentType, string fileName, bool expected) =>
        Assert.Equal(expected, Reader.CanRead(contentType, fileName));

    /// <summary>Answers every question by citing the first document.</summary>
    private sealed class CiteFirstChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Notes stay available offline [1].")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Notes stay available offline [1].");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
