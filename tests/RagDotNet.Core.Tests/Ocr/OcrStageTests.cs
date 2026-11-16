using System.Diagnostics;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Diagnostics;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Ocr;
using RagDotNet.TestAssetGen;

namespace RagDotNet.Core.Tests.Ocr;

public class OcrStageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DocumentBlock P(string text, int? page, params string[] path) => new(BlockKind.Paragraph, text, path, page);

    private static ImageInput Img(byte b, int? w = 100, int? h = 100) => new(new[] { b, b, b }, "image/png", w, h);

    private static OcrStage Stage(IOcrProvider? images = null, IDocumentOcrProvider? documents = null, IPageRenderer[]? renderers = null, Action<OcrOptions>? configure = null)
    {
        var options = new OcrOptions();
        configure?.Invoke(options);
        return new OcrStage(images, documents, renderers ?? [], options);
    }

    private static Task<ParsedDocument> Read(OcrStage stage, ParsedDocument parsed, string fileName = "a.pdf") =>
        stage.ReadAsync(new FixedReader(parsed), [1, 2, 3], new DocumentReadContext(fileName, null), Ct);

    [Fact]
    public async Task Scanned_page_text_is_inserted_at_the_page_with_its_page_number_and_heading_path()
    {
        var ocr = new FakeOcr(_ => new OcrResult("two", 0.9));
        var renderer = new FakeRenderer();
        var parsed = new ParsedDocument([new(BlockKind.Heading, "Intro", ["Intro"], 1), P("one", 1, "Intro"), P("three", 3, "Intro")])
        {
            OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 2, 2)],
        };

        var result = await Read(Stage(ocr, renderers: [renderer]), parsed);

        Assert.Equal(["Intro", "one", "two", "three"], result.Blocks.Select(b => b.Text));
        Assert.Equal((BlockKind.Paragraph, 2, "Intro"), (result.Blocks[2].Kind, result.Blocks[2].PageNumber!.Value, result.Blocks[2].HeadingPath.Single()));
        Assert.Empty(result.Warnings);
        Assert.Equal([(2, 300)], renderer.Calls);
    }

    [Fact]
    public async Task Scanned_page_without_a_renderer_names_the_rendering_package()
    {
        var parsed = new ParsedDocument([P("one", 1)]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 2)] };

        var result = await Read(Stage(new FakeOcr(_ => new OcrResult("x", 1))), parsed);

        Assert.Equal(
            [new IngestWarning(IngestWarningCodes.PageWithoutText, "Page 2 has no extractable text; add RagDotNet.Readers.Pdf.Rendering (AddPdfPageRendering) so the OCR provider can read it.", 2)],
            result.Warnings);
    }

    [Fact]
    public async Task Embedded_image_text_is_inserted_at_its_position()
    {
        var parsed = new ParsedDocument([P("before", null, "Setup"), P("after", null, "Setup")])
        {
            OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 1, null, Img(1))],
        };

        var result = await Read(Stage(new FakeOcr(_ => new OcrResult("Click Sync now", 0.95))), parsed);

        Assert.Equal(["before", "Click Sync now", "after"], result.Blocks.Select(b => b.Text));
        Assert.Equal(["Setup"], result.Blocks[1].HeadingPath);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Images_below_64px_are_skipped_using_header_size_when_the_reader_gave_none()
    {
        var ocr = new FakeOcr(i => new OcrResult("read " + i.Bytes.Length, 1));
        var parsed = new ParsedDocument([P("text", null)])
        {
            OcrCandidates =
            [
                new OcrCandidate(OcrCandidateKind.Image, 1, null, new ImageInput(Png.Solid(32, 200, 0x80), "image/png")), // narrow, size from header
                new OcrCandidate(OcrCandidateKind.Image, 1, null, new ImageInput(Png.Solid(200, 200, 0x80), "image/png", 10, 10)), // reader's size wins
                new OcrCandidate(OcrCandidateKind.Image, 1, null, new ImageInput(new byte[] { 9, 9, 9, 9 }, "image/webp")), // unknown size: read
            ],
        };

        var result = await Read(Stage(ocr), parsed);

        Assert.Equal(1, ocr.Calls);
        Assert.Equal(["text", "read 4"], result.Blocks.Select(b => b.Text));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Identical_images_are_recognised_once_and_inserted_once()
    {
        var ocr = new FakeOcr(_ => new OcrResult("Quill logo", 0.9));
        var parsed = new ParsedDocument([P("a", null), P("b", null)])
        {
            OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 1, null, Img(7)), new OcrCandidate(OcrCandidateKind.Image, 2, null, Img(7))],
        };

        var result = await Read(Stage(ocr), parsed);

        Assert.Equal(1, ocr.Calls);
        Assert.Equal(["a", "Quill logo", "b"], result.Blocks.Select(b => b.Text));
    }

    [Fact]
    public async Task Page_background_images_are_not_recognised()
    {
        var ocr = new FakeOcr(_ => new OcrResult("scan text", 0.9));
        var parsed = new ParsedDocument([P("layer text", 1)])
        {
            OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 0, 1, Img(1)) { IsPageBackground = true }],
        };

        var result = await Read(Stage(ocr), parsed);

        Assert.Equal(0, ocr.Calls);
        Assert.Equal(["layer text"], result.Blocks.Select(b => b.Text));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Stamp_pages_are_rendered_and_read()
    {
        var ocr = new FakeOcr(_ => new OcrResult("scanned body text", 0.9));
        var parsed = new ParsedDocument([P("ABC-000123", 1)])
        {
            OcrCandidates =
            [
                new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 1) { HasTextLayer = true },
                new OcrCandidate(OcrCandidateKind.Image, 0, 1, Img(1)) { IsPageBackground = true },
            ],
        };

        var result = await Read(Stage(ocr, renderers: [new FakeRenderer()]), parsed);

        Assert.Equal(1, ocr.Calls);
        Assert.Equal(["ABC-000123", "scanned body text"], result.Blocks.Select(b => b.Text));
    }

    [Fact]
    public async Task Embedded_images_off_reads_none_and_warns_none()
    {
        var ocr = new FakeOcr(_ => new OcrResult("page text", 0.9));
        var parsed = new ParsedDocument([P("one", 1)])
        {
            OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 1, 1, Img(1)), new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 2)],
        };

        var result = await Read(Stage(ocr, renderers: [new FakeRenderer()], configure: o => o.OcrEmbeddedImages = false), parsed);

        Assert.Equal(1, ocr.Calls);
        Assert.Equal(["one", "page text"], result.Blocks.Select(b => b.Text));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Low_confidence_text_is_kept_with_a_warning()
    {
        var parsed = new ParsedDocument([]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 0, 2)] };

        var result = await Read(Stage(new FakeOcr(_ => new OcrResult("blurry", 0.3)), renderers: [new FakeRenderer()]), parsed);

        Assert.Equal(["blurry"], result.Blocks.Select(b => b.Text));
        Assert.Equal([new IngestWarning(IngestWarningCodes.OcrLowConfidence, "Page 2: OCR text has low confidence (0.30); it was kept.", 2)], result.Warnings);
    }

    [Fact]
    public async Task Empty_ocr_text_adds_nothing()
    {
        var parsed = new ParsedDocument([P("a", null)]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 1, null, Img(1))] };

        var result = await Read(Stage(new FakeOcr(_ => new OcrResult("  \n ", 0.1))), parsed);

        Assert.Equal(["a"], result.Blocks.Select(b => b.Text));
        Assert.Empty(result.Warnings);
    }

    [Fact] // Review Focus 3
    public async Task One_failing_image_is_a_warning_and_the_rest_is_read()
    {
        var ocr = new FakeOcr(i => i.Bytes.Span[0] == 1 ? throw new InvalidOperationException("bad image") : new OcrResult("good", 0.9));
        var parsed = new ParsedDocument([P("a", null)])
        {
            OcrCandidates =
            [
                new OcrCandidate(OcrCandidateKind.Image, 1, null, Img(1)),
                new OcrCandidate(OcrCandidateKind.Image, 1, 4, null),
                new OcrCandidate(OcrCandidateKind.Image, 1, null, Img(2)),
            ],
        };

        var result = await Read(Stage(ocr), parsed);

        Assert.Equal(["a", "good"], result.Blocks.Select(b => b.Text));
        Assert.Equal(
            [
                new IngestWarning(IngestWarningCodes.OcrFailed, "OCR failed: bad image"),
                new IngestWarning(IngestWarningCodes.OcrFailed, "Page 4: an image could not be extracted for OCR.", 4),
            ],
            result.Warnings);
    }

    [Fact] // Review Focus 5
    public async Task Renderer_failure_on_a_page_is_an_OcrFailed_warning()
    {
        var parsed = new ParsedDocument([P("one", 1)])
        {
            OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 2), new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 3)],
        };

        var result = await Read(Stage(new FakeOcr(_ => new OcrResult("page three", 0.9)), renderers: [new FakeRenderer(failPage: 2)]), parsed);

        Assert.Equal(["one", "page three"], result.Blocks.Select(b => b.Text));
        Assert.Equal([new IngestWarning(IngestWarningCodes.OcrFailed, "Page 2: OCR failed: render error", 2)], result.Warnings);
    }

    [Fact]
    public async Task OcrUnavailable_propagates()
    {
        var parsed = new ParsedDocument([]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 0, null, Img(1))] };

        await Assert.ThrowsAsync<OcrUnavailableException>(() => Read(Stage(new FakeOcr(_ => throw new OcrUnavailableException("install it"))), parsed));
    }

    [Fact] // Review Focus 2
    public async Task Cancellation_during_ocr_propagates_and_nothing_is_stored()
    {
        var started = new TaskCompletionSource();
        var waiting = new FakeOcr(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new OcrResult("never", 1);
        });
        var parsed = new ParsedDocument([P("text", null)]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 1, null, Img(1))] };
        var (rag, _, _, _, store) = TestRag.Create(builder: b => b.AddReader(new FixedReader(parsed)).UseOcrProvider(waiting));
        using var cts = new CancellationTokenSource();

        var ingest = rag.IngestAsync(new DocumentSource("doc", new MemoryStream([1]), "doc.bin"), cancellationToken: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingest);
        var again = new Configuration.RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64)).UseVectorStore(store, "test", 64)
            .AddReader(new FixedReader(parsed)).UseOcrProvider(new FakeOcr(_ => new OcrResult("now", 1))).Build();
        Assert.Equal(IngestStatus.Created, (await again.IngestAsync(new DocumentSource("doc", new MemoryStream([1]), "doc.bin"), cancellationToken: Ct)).Status);
    }

    [Fact] // Review Focus 4
    public async Task Fill_mode_fills_only_scanned_pages_and_ignores_other_blocks()
    {
        var images = new FakeOcr(_ => new OcrResult("from image provider", 1));
        var documents = new FakeDocumentOcr(_ => new DocumentOcrResult(
        [
            P("x1", 1), P("x2a", 2), new(BlockKind.List, "- x2b", [], 2), P("x3", 3), P("xn", null),
        ]));
        var parsed = new ParsedDocument([P("one", 1, "H"), P("three", 3, "H")]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 2)] };

        var result = await Read(Stage(images, documents, [new FakeRenderer()]), parsed);

        Assert.Equal(["one", "x2a", "- x2b", "three"], result.Blocks.Select(b => b.Text));
        Assert.Equal([BlockKind.Paragraph, BlockKind.List], result.Blocks.Skip(1).Take(2).Select(b => b.Kind));
        Assert.All(result.Blocks.Skip(1).Take(2), b => Assert.Equal((2, "H"), (b.PageNumber!.Value, b.HeadingPath.Single())));
        Assert.Equal(0, images.Calls);
        Assert.Equal(1, documents.Calls);
    }

    [Fact]
    public async Task Fill_mode_failure_falls_back_to_the_image_provider()
    {
        var documents = new FakeDocumentOcr(_ => throw new InvalidOperationException("boom"));
        var parsed = new ParsedDocument([P("one", 1)]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 2)] };

        var result = await Read(Stage(new FakeOcr(_ => new OcrResult("page two", 0.9)), documents, [new FakeRenderer()]), parsed);

        Assert.Equal(["one", "page two"], result.Blocks.Select(b => b.Text));
        Assert.Equal([new IngestWarning(IngestWarningCodes.OcrFailed, "The document OCR provider failed on 'a.pdf': boom")], result.Warnings);
    }

    [Fact]
    public async Task Fill_mode_is_not_called_without_scanned_pages()
    {
        var documents = new FakeDocumentOcr(_ => new DocumentOcrResult([P("x", 1)]));

        var result = await Read(Stage(documents: documents), new ParsedDocument([P("one", 1)]));

        Assert.Equal(0, documents.Calls);
        Assert.Equal(["one"], result.Blocks.Select(b => b.Text));
    }

    [Fact]
    public async Task Replace_mode_uses_the_provider_output_for_formats_it_claims()
    {
        var documents = new FakeDocumentOcr(_ => new DocumentOcrResult([P("from service", 1)]) { Warnings = [new IngestWarning("Service", "note")] }, canProcess: f => f.EndsWith(".pdf", StringComparison.Ordinal));
        var stage = Stage(documents: documents, configure: o => o.DocumentProviderMode = DocumentOcrMode.ReplaceReader);
        var parsed = new ParsedDocument([P("from reader", 1)]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 2)] };

        var replaced = await Read(stage, parsed, "a.pdf");
        var kept = await Read(stage, new ParsedDocument([P("from reader", null)]), "a.docx");

        Assert.Equal(["from service"], replaced.Blocks.Select(b => b.Text));
        Assert.Equal(["Service"], replaced.Warnings.Select(w => w.Code));
        Assert.Empty(replaced.OcrCandidates);
        Assert.Equal(["from reader"], kept.Blocks.Select(b => b.Text));
    }

    [Fact]
    public async Task Replace_mode_failure_falls_back_to_the_reader_with_a_warning()
    {
        var documents = new FakeDocumentOcr(_ => throw new InvalidOperationException("boom"));
        var stage = Stage(documents: documents, configure: o => o.DocumentProviderMode = DocumentOcrMode.ReplaceReader);

        var result = await Read(stage, new ParsedDocument([P("from reader", 1)]));

        Assert.Equal(["from reader"], result.Blocks.Select(b => b.Text));
        Assert.Equal([new IngestWarning(IngestWarningCodes.OcrFailed, "The document OCR provider failed on 'a.pdf'; read with the built-in reader instead: boom")], result.Warnings);
    }

    [Fact]
    public async Task Replace_mode_never_adds_formats_no_reader_handles()
    {
        var documents = new FakeDocumentOcr(_ => new DocumentOcrResult([P("png text", 1)]));
        var (rag, _, _, _, _) = TestRag.Create(o => o.Ocr.DocumentProviderMode = DocumentOcrMode.ReplaceReader, b => b.UseDocumentOcrProvider(documents));

        await Assert.ThrowsAsync<UnsupportedDocumentException>(() => rag.IngestAsync(new DocumentSource("scan", new MemoryStream([1, 2]), "scan.png"), cancellationToken: Ct));
    }

    [Fact]
    public async Task Only_a_document_provider_leaves_images_as_ImageNotRead_naming_a_per_image_provider()
    {
        var parsed = new ParsedDocument([P("a", null)]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 1, null, Img(1))] };

        var result = await Read(Stage(documents: new FakeDocumentOcr(_ => new DocumentOcrResult([]))), parsed, "a.docx");

        Assert.Equal(
            [new IngestWarning(IngestWarningCodes.ImageNotRead, "1 embedded image(s) were not read; register a per-image OCR provider (IOcrProvider) to read them.")],
            result.Warnings);
    }

    [Fact]
    public void Fingerprint_changes_with_provider_mode_and_options_but_not_min_confidence()
    {
        var a = new FakeOcr(_ => new OcrResult("", 1), "a");
        var baseline = Stage(a).Fingerprint;

        Assert.NotEqual("none", baseline);
        Assert.NotEqual(baseline, Stage(new FakeOcr(_ => new OcrResult("", 1), "b")).Fingerprint);
        Assert.NotEqual(baseline, Stage(a, renderers: [new FakeRenderer()]).Fingerprint);
        Assert.NotEqual(baseline, Stage(a, configure: o => o.OcrEmbeddedImages = false).Fingerprint);
        Assert.NotEqual(baseline, Stage(a, configure: o => o.MinImageSize = 10).Fingerprint);
        Assert.NotEqual(baseline, Stage(a, configure: o => o.PageDpi = 200).Fingerprint);
        Assert.NotEqual(Stage(documents: new FakeDocumentOcr(_ => new DocumentOcrResult([]))).Fingerprint,
            Stage(documents: new FakeDocumentOcr(_ => new DocumentOcrResult([])), configure: o => o.DocumentProviderMode = DocumentOcrMode.ReplaceReader).Fingerprint);
        Assert.Equal(baseline, Stage(a, configure: o => o.MinConfidence = 0.9).Fingerprint);
    }

    [Fact]
    public async Task Ocr_work_is_traced_as_rag_ocr()
    {
        using var source = new ActivitySource("RagDotNet.Tests.Ocr");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is RagTelemetry.ActivitySourceName or "RagDotNet.Tests.Ocr",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        using var root = source.StartActivity("test")!;
        var parsed = new ParsedDocument([]) { OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 0, null, Img(1))] };

        await Read(Stage(new FakeOcr(_ => new OcrResult("x", 1))), parsed);
        await Read(Stage(), parsed);

        lock (spans) Assert.Single(spans, s => s.TraceId == root.TraceId && s.OperationName == "rag.ocr");
    }

    private sealed class FixedReader(ParsedDocument document) : IDocumentReader
    {
        public bool CanRead(string? contentType, string fileName) => !fileName.EndsWith(".png", StringComparison.Ordinal);

        public Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken) => Task.FromResult(document);
    }

    private sealed class FakeOcr : IOcrProvider
    {
        private readonly Func<ImageInput, CancellationToken, Task<OcrResult>> _recognize;
        private int _calls;

        public FakeOcr(Func<ImageInput, OcrResult> recognize, string id = "fake") : this((i, _) => Task.FromResult(recognize(i)), id)
        {
        }

        public FakeOcr(Func<ImageInput, CancellationToken, Task<OcrResult>> recognize, string id = "fake")
        {
            _recognize = recognize;
            Id = id;
        }

        public string Id { get; }

        public int Calls => _calls;

        public Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _recognize(image, cancellationToken);
        }
    }

    private sealed class FakeRenderer(int? failPage = null) : IPageRenderer
    {
        public List<(int Page, int Dpi)> Calls { get; } = [];

        public bool CanRender(string? contentType, string fileName) => true;

        public Task<ImageInput> RenderPageAsync(ReadOnlyMemory<byte> document, int pageNumber, int dpi, CancellationToken cancellationToken)
        {
            Calls.Add((pageNumber, dpi));
            if (pageNumber == failPage) throw new InvalidOperationException("render error");
            return Task.FromResult(new ImageInput(new[] { (byte)pageNumber }, "image/png", 2480, 3508));
        }
    }

    private sealed class FakeDocumentOcr(Func<ReadOnlyMemory<byte>, DocumentOcrResult> process, Func<string, bool>? canProcess = null) : IDocumentOcrProvider
    {
        public int Calls { get; private set; }

        public string Id => "doc";

        public bool CanProcess(string? contentType, string fileName) => canProcess?.Invoke(fileName) ?? true;

        public Task<DocumentOcrResult> ProcessAsync(ReadOnlyMemory<byte> document, DocumentReadContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(process(document));
        }
    }
}
