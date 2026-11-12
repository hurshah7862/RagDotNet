using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Ocr;
using RagDotNet.Pipeline;

namespace RagDotNet.Core.Tests.Ocr;

/// <summary>Documents with unread pages or images are read again when the OCR setup changes; others stay Unchanged.</summary>
public class OcrReingestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ParsedDocument Scanned = new([new DocumentBlock(BlockKind.Paragraph, "Cover page.", [], 1)])
    {
        OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 2)],
    };

    private static readonly ParsedDocument Plain = new([new DocumentBlock(BlockKind.Paragraph, "Plain text only.", [], 1)]);

    private static IRagPipeline Pipeline(InMemoryVectorStore store, ParsedDocument document, IOcrProvider? ocr)
    {
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(store, "test", 64)
            .AddReader(new FixedReader(document));
        if (ocr is not null) builder.UseOcrProvider(ocr).AddPageRenderer(new FakeRenderer());
        return builder.Build();
    }

    private static Task<IngestResult> Ingest(IRagPipeline rag) =>
        rag.IngestAsync(new DocumentSource("doc", new MemoryStream([1, 2, 3]), "doc.bin"), cancellationToken: Ct);

    [Fact]
    public async Task Document_with_a_scanned_page_is_reread_after_ocr_is_registered()
    {
        var store = new InMemoryVectorStore();
        Assert.Equal(IngestStatus.Created, (await Ingest(Pipeline(store, Scanned, null))).Status);

        var withOcr = Pipeline(store, Scanned, new FakeOcr("Quarterly revenue grew."));
        var result = await Ingest(withOcr);

        Assert.Equal(IngestStatus.Updated, result.Status);
        Assert.Contains(await withOcr.RetrieveAsync("Quarterly revenue grew.", cancellationToken: Ct), c => c.Text.Contains("Quarterly revenue grew.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Document_without_candidates_stays_Unchanged_after_ocr_is_registered()
    {
        var store = new InMemoryVectorStore();
        await Ingest(Pipeline(store, Plain, null));

        Assert.Equal(IngestStatus.Unchanged, (await Ingest(Pipeline(store, Plain, new FakeOcr("x")))).Status);
    }

    [Fact]
    public async Task Same_ocr_setup_is_Unchanged()
    {
        var store = new InMemoryVectorStore();
        await Ingest(Pipeline(store, Scanned, new FakeOcr("text")));

        Assert.Equal(IngestStatus.Unchanged, (await Ingest(Pipeline(store, Scanned, new FakeOcr("text")))).Status);
    }

    [Fact]
    public async Task Removing_ocr_rereads_documents_that_were_read_with_it()
    {
        var store = new InMemoryVectorStore();
        await Ingest(Pipeline(store, Scanned, new FakeOcr("text")));

        var result = await Ingest(Pipeline(store, Scanned, null));

        Assert.Equal(IngestStatus.Updated, result.Status);
        Assert.Contains(result.Warnings, w => w.Code == IngestWarningCodes.PageWithoutText);
    }

    [Fact]
    public void Without_ocr_the_stored_hash_equals_stage5()
    {
        Assert.Equal("abc", Ingestor.StoredChunkingHash("abc", hadCandidates: false, "none"));
        Assert.Equal("abc", Ingestor.StoredChunkingHash("abc", hadCandidates: false, "tesseract|x"));
        Assert.Equal("abc+ocr:none", Ingestor.StoredChunkingHash("abc", hadCandidates: true, "none"));
    }

    [Fact]
    public async Task Stored_hash_of_a_plain_document_is_the_plain_chunking_hash()
    {
        var store = new InMemoryVectorStore();
        var rag = Pipeline(store, Plain, new FakeOcr("x"));
        await Ingest(rag);

        var engine = ((RagPipeline)rag).Engine;
        var manifest = await engine.Manifests.GetAsync("", "doc", Ct);

        Assert.DoesNotContain("+ocr:", manifest!.ChunkingHash, StringComparison.Ordinal);
    }

    private sealed class FixedReader(ParsedDocument document) : IDocumentReader
    {
        public bool CanRead(string? contentType, string fileName) => true;

        public Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken) => Task.FromResult(document);
    }

    private sealed class FakeOcr(string text) : IOcrProvider
    {
        public string Id => "fake";

        public Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken) => Task.FromResult(new OcrResult(text, 0.9));
    }

    private sealed class FakeRenderer : IPageRenderer
    {
        public bool CanRender(string? contentType, string fileName) => true;

        public Task<ImageInput> RenderPageAsync(ReadOnlyMemory<byte> document, int pageNumber, int dpi, CancellationToken cancellationToken) =>
            Task.FromResult(new ImageInput(new byte[] { 1 }, "image/png"));
    }
}
