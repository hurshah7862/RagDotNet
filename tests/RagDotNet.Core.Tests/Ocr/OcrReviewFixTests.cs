using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Ocr;
using RagDotNet.Pipeline;

namespace RagDotNet.Core.Tests.Ocr;

/// <summary>Fixes from the stage 6 final review.</summary>
public class OcrReviewFixTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImageInput Img(byte b) => new(new[] { b, b, b }, "image/png", 100, 100);

    private static OcrStage Stage(IOcrProvider? images = null, IPageRenderer[]? renderers = null, Action<OcrOptions>? configure = null, IDocumentOcrProvider? documents = null)
    {
        var options = new OcrOptions();
        configure?.Invoke(options);
        return new OcrStage(images, documents, renderers ?? [], options);
    }

    [Fact] // Important 1: a document with an OCR failure was stored as done and never retried
    public async Task A_document_with_an_ocr_failure_is_read_again_on_the_next_ingest()
    {
        var store = new InMemoryVectorStore();
        var parsed = new ParsedDocument([new DocumentBlock(BlockKind.Paragraph, "Cover.", [], 1)])
        {
            OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 1, 1, Img(1))],
        };
        var flaky = new Ocr(_ => throw new HttpRequestException("429 Too Many Requests"));
        IRagPipeline Pipeline() => new RagPipelineBuilder().UseEmbeddingGenerator(new FakeEmbeddingGenerator(64)).UseVectorStore(store, "t", 64)
            .AddReader(new FixedReader(parsed)).UseOcrProvider(flaky).Build();

        var first = await Pipeline().IngestAsync(Source(), cancellationToken: Ct);
        flaky.Recognize = _ => new OcrResult("Quarterly chart", 0.9);
        var second = await Pipeline().IngestAsync(Source(), cancellationToken: Ct);
        var third = await Pipeline().IngestAsync(Source(), cancellationToken: Ct);

        Assert.Contains(first.Warnings, w => w.Code == IngestWarningCodes.OcrFailed);
        Assert.Equal(IngestStatus.Updated, second.Status);
        Assert.Equal(IngestStatus.Unchanged, third.Status);
    }

    [Theory] // Important 2: readers extracted every image even when nothing would read it
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Readers_are_asked_to_extract_images_only_when_a_provider_reads_them(bool provider, bool embeddedImages, bool expected)
    {
        var reader = new FixedReader(new ParsedDocument([]));
        var stage = Stage(provider ? new Ocr(_ => new OcrResult("", 1)) : null, configure: o => o.OcrEmbeddedImages = embeddedImages);

        await stage.ReadAsync(reader, [], new DocumentReadContext("a.pdf", null), Ct);

        Assert.Equal(expected, reader.Context!.ExtractImages);
    }

    [Fact]
    public void Reading_contexts_extract_images_by_default() =>
        Assert.True(new DocumentReadContext("a.pdf", null).ExtractImages);

    [Fact] // Important 3: a stamped scan with a provider but no renderer was dropped without a warning
    public async Task A_stamp_page_without_a_renderer_is_read_from_its_page_image()
    {
        var parsed = new ParsedDocument([new DocumentBlock(BlockKind.Paragraph, "ABC-000123", [], 1)])
        {
            OcrCandidates =
            [
                new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 1) { HasTextLayer = true },
                new OcrCandidate(OcrCandidateKind.Image, 0, 1, Img(1)) { IsPageBackground = true },
            ],
        };

        var result = await Stage(new Ocr(_ => new OcrResult("The scanned body text.", 0.9))).ReadAsync(new FixedReader(parsed), [], new DocumentReadContext("a.pdf", null), Ct);

        Assert.Equal(["ABC-000123", "The scanned body text."], result.Blocks.Select(b => b.Text));
        Assert.Empty(result.Warnings);
    }

    [Fact] // Important 3, when the page image could not be extracted either
    public async Task A_stamp_page_without_a_renderer_or_image_is_a_warning()
    {
        var parsed = new ParsedDocument([new DocumentBlock(BlockKind.Paragraph, "ABC-000123", [], 1)])
        {
            OcrCandidates =
            [
                new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 1) { HasTextLayer = true },
                new OcrCandidate(OcrCandidateKind.Image, 0, 1, null) { IsPageBackground = true },
            ],
        };

        var result = await Stage(new Ocr(_ => new OcrResult("x", 0.9))).ReadAsync(new FixedReader(parsed), [], new DocumentReadContext("a.pdf", null), Ct);

        Assert.Contains(result.Warnings, w => w.Code == IngestWarningCodes.PageWithoutText && w.PageNumber == 1 && w.Message.Contains("RagDotNet.Readers.Pdf.Rendering", StringComparison.Ordinal));
    }

    [Fact] // Minor 6: a provider that ignores the token let a cancelled ingest carry on
    public async Task Cancellation_requested_during_a_call_that_ignores_it_still_stops()
    {
        using var cts = new CancellationTokenSource();
        var parsed = new ParsedDocument([]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 0, null, Img(1))] };
        var ocr = new Ocr(_ =>
        {
            cts.Cancel();
            return new OcrResult("finished anyway", 1);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stage(ocr).ReadAsync(new FixedReader(parsed), [], new DocumentReadContext("a.pdf", null), cts.Token));
    }

    [Fact] // Minor 7: a provider's own timeout aborted the ingest
    public async Task A_provider_timeout_is_an_OcrFailed_warning()
    {
        var parsed = new ParsedDocument([]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 0, 3, Img(1))] };
        var ocr = new Ocr(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var result = await Stage(ocr).ReadAsync(new FixedReader(parsed), [], new DocumentReadContext("a.pdf", null), Ct);

        Assert.Equal(IngestWarningCodes.OcrFailed, Assert.Single(result.Warnings).Code);
    }

    [Fact] // Minor 8: vector images (SVG, EMF, WMF) were sent to OCR and each failed
    public async Task Vector_images_are_not_sent_to_ocr_and_are_reported_once()
    {
        var calls = 0;
        var parsed = new ParsedDocument([])
        {
            OcrCandidates =
            [
                new OcrCandidate(OcrCandidateKind.Image, 0, null, new ImageInput(new byte[] { 1 }, "image/svg+xml")),
                new OcrCandidate(OcrCandidateKind.Image, 0, null, new ImageInput(new byte[] { 2 }, "image/x-emf")),
                new OcrCandidate(OcrCandidateKind.Image, 0, null, new ImageInput(new byte[] { 3 }, "image/x-wmf")),
            ],
        };

        var result = await Stage(new Ocr(_ => { calls++; return new OcrResult("x", 1); })).ReadAsync(new FixedReader(parsed), [], new DocumentReadContext("a.docx", null), Ct);

        Assert.Equal(0, calls);
        Assert.Equal(
            [new IngestWarning(IngestWarningCodes.ImageNotRead, "3 vector image(s) (SVG, EMF or WMF) were not read; OCR reads raster images only.")],
            result.Warnings);
    }

    [Fact] // Minor 11: whitespace-only blocks from a whole-document provider were inserted
    public async Task Fill_mode_ignores_whitespace_only_blocks()
    {
        var documents = new DocumentOcr(new DocumentOcrResult([new DocumentBlock(BlockKind.Paragraph, "  \n ", [], 2), new DocumentBlock(BlockKind.Paragraph, "real", [], 2)]));
        var parsed = new ParsedDocument([]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 0, 2)] };

        var result = await Stage(documents: documents).ReadAsync(new FixedReader(parsed), [], new DocumentReadContext("a.pdf", null), Ct);

        Assert.Equal(["real"], result.Blocks.Select(b => b.Text));
    }

    private static DocumentSource Source() => new("doc", new MemoryStream([1, 2, 3]), "doc.bin");

    private sealed class FixedReader(ParsedDocument document) : IDocumentReader
    {
        public DocumentReadContext? Context { get; private set; }

        public bool CanRead(string? contentType, string fileName) => true;

        public Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.FromResult(document);
        }
    }

    private sealed class Ocr(Func<ImageInput, OcrResult> recognize) : IOcrProvider
    {
        public Func<ImageInput, OcrResult> Recognize { get; set; } = recognize;

        public string Id => "fake";

        public Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken) => Task.FromResult(Recognize(image));
    }

    private sealed class DocumentOcr(DocumentOcrResult result) : IDocumentOcrProvider
    {
        public string Id => "doc";

        public bool CanProcess(string? contentType, string fileName) => true;

        public Task<DocumentOcrResult> ProcessAsync(ReadOnlyMemory<byte> document, DocumentReadContext context, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
