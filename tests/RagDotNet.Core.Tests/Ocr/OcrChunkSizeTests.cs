using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Ocr;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;

namespace RagDotNet.Core.Tests.Ocr;

/// <summary><see cref="OcrOptions.ChunkMaxTokens"/> chunks text that came from OCR at its own size, and only that text.</summary>
public class OcrChunkSizeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // About 320 tokens each: one chunk at the default 512, several at 64.
    private static readonly string TextLayer = Sentences("Typed", 40);
    private static readonly string Recognised = Sentences("Scanned", 40);

    /// <summary>Page 1 has a text layer; page 2 is a scan.</summary>
    private static ParsedDocument MixedPdf() => new([new DocumentBlock(BlockKind.Paragraph, TextLayer, [], 1)])
    {
        OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 2)],
    };

    private static readonly ParsedDocument Plain = new([new DocumentBlock(BlockKind.Paragraph, TextLayer, [], 1)]);

    private static string Sentences(string prefix, int count) =>
        string.Join(" ", Enumerable.Range(1, count).Select(i => $"{prefix} sentence number {i} is here."));

    private static IRagPipeline Pipeline(InMemoryVectorStore store, ParsedDocument document, int? ocrChunkMaxTokens, Action<RagOptions>? configure = null,
        IDocumentOcrProvider? documentOcr = null)
    {
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(store, "test", 64)
            .AddReader(new FixedReader(document))
            .Configure(o =>
            {
                o.Hybrid = HybridMode.Off;
                o.Ocr.ChunkMaxTokens = ocrChunkMaxTokens;
                configure?.Invoke(o);
            });
        if (documentOcr is not null) builder.UseDocumentOcrProvider(documentOcr);
        else builder.UseOcrProvider(new FakeOcr(Recognised)).AddPageRenderer(new FakeRenderer());
        return builder.Build();
    }

    private static Task<IngestResult> Ingest(IRagPipeline rag, IngestOptions? options = null) =>
        rag.IngestAsync(new DocumentSource("doc", new MemoryStream([1, 2, 3]), "doc.pdf"), options, cancellationToken: Ct);

    private static async Task<List<RetrievedChunk>> Chunks(IRagPipeline rag) =>
        (await rag.RetrieveAsync("sentence", new RetrievalOptions { TopK = 100 }, Ct)).OrderBy(c => c.ChunkIndex).ToList();

    private static int Tokens(IRagPipeline rag, string text) => ((RagPipeline)rag).Engine.Tokens.Count(text);

    [Fact]
    public void ChunkMaxTokens_is_unset_by_default()
    {
        Assert.Null(new RagOptions().Ocr.ChunkMaxTokens);
    }

    [Fact]
    public async Task Ocr_text_is_chunked_at_ChunkMaxTokens_and_the_text_layer_at_MaxTokens()
    {
        var rag = Pipeline(new InMemoryVectorStore(), MixedPdf(), ocrChunkMaxTokens: 64);
        await Ingest(rag);
        var chunks = await Chunks(rag);

        var scanned = chunks.Where(c => c.Text.Contains("Scanned", StringComparison.Ordinal)).ToList();
        var typed = chunks.Where(c => c.Text.Contains("Typed", StringComparison.Ordinal)).ToList();
        Assert.True(scanned.Count > 1, $"expected the OCR text in several chunks, got {scanned.Count}");
        Assert.All(scanned, c => Assert.True(Tokens(rag, c.Text) <= 64, $"OCR chunk of {Tokens(rag, c.Text)} tokens"));
        Assert.Single(typed);
        Assert.Equal(TextLayer, typed[0].Text);
        Assert.DoesNotContain(chunks, c => c.Text.Contains("Scanned", StringComparison.Ordinal) && c.Text.Contains("Typed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unset_option_chunks_ocr_text_like_any_other_text()
    {
        var rag = Pipeline(new InMemoryVectorStore(), MixedPdf(), ocrChunkMaxTokens: null);
        await Ingest(rag);

        // The whole page stays in one chunk (which starts with the overlap carried over from the text layer).
        var scanned = (await Chunks(rag)).Where(c => c.Text.Contains("Scanned", StringComparison.Ordinal)).ToList();
        Assert.Single(scanned);
        Assert.EndsWith(Recognised, scanned[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chunk_indexes_are_continuous_and_pages_are_kept()
    {
        var rag = Pipeline(new InMemoryVectorStore(), MixedPdf(), ocrChunkMaxTokens: 64);
        var result = await Ingest(rag);
        var chunks = await Chunks(rag);

        Assert.Equal(Enumerable.Range(0, result.ChunkCount), chunks.Select(c => c.ChunkIndex));
        Assert.Equal(1, chunks[0].PageStart);
        Assert.All(chunks.Skip(1), c => Assert.Equal(2, c.PageStart));
    }

    [Fact]
    public async Task Text_read_from_an_embedded_image_uses_the_ocr_size_and_the_paragraphs_around_it_do_not()
    {
        var document = new ParsedDocument(
        [
            new DocumentBlock(BlockKind.Paragraph, TextLayer, ["Report"]),
            new DocumentBlock(BlockKind.Paragraph, "Closing words of the report.", ["Report"]),
        ])
        {
            OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 1, null, new ImageInput(new byte[] { 1 }, "image/png", 500, 500))],
        };
        var rag = Pipeline(new InMemoryVectorStore(), document, ocrChunkMaxTokens: 64);
        await Ingest(rag);
        var chunks = await Chunks(rag);

        var scanned = chunks.Where(c => c.Text.Contains("Scanned", StringComparison.Ordinal)).ToList();
        Assert.True(scanned.Count > 1);
        Assert.All(scanned, c => Assert.True(Tokens(rag, c.Text) <= 64));
        Assert.Contains(chunks, c => c.Text == TextLayer);
        Assert.Contains(chunks, c => c.Text.Contains("Closing words of the report.", StringComparison.Ordinal) && !c.Text.Contains("Scanned", StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.Equal(["Report"], c.HeadingPath));
    }

    [Fact]
    public async Task Whole_document_provider_text_in_ReplaceReader_mode_uses_the_ocr_size()
    {
        var provider = new DocumentOcr(new DocumentOcrResult([new DocumentBlock(BlockKind.Paragraph, Recognised, [], 1)]));
        var rag = Pipeline(new InMemoryVectorStore(), Plain, ocrChunkMaxTokens: 64,
            configure: o => o.Ocr.DocumentProviderMode = DocumentOcrMode.ReplaceReader, documentOcr: provider);
        await Ingest(rag);
        var chunks = await Chunks(rag);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.Contains("Scanned", c.Text, StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.True(Tokens(rag, c.Text) <= 64));
    }

    [Fact]
    public async Task Whole_document_provider_text_that_fills_missing_pages_uses_the_ocr_size()
    {
        var provider = new DocumentOcr(new DocumentOcrResult([new DocumentBlock(BlockKind.Paragraph, Recognised, [], 2)]));
        var rag = Pipeline(new InMemoryVectorStore(), MixedPdf(), ocrChunkMaxTokens: 64, documentOcr: provider);
        await Ingest(rag);
        var chunks = await Chunks(rag);

        Assert.True(chunks.Count(c => c.Text.Contains("Scanned", StringComparison.Ordinal)) > 1);
        Assert.Single(chunks, c => c.Text == TextLayer);
    }

    [Fact]
    public async Task Per_call_chunking_options_keep_the_ocr_size_for_ocr_text()
    {
        var rag = Pipeline(new InMemoryVectorStore(), MixedPdf(), ocrChunkMaxTokens: 64);
        await Ingest(rag, new IngestOptions { Chunking = new ChunkingOptions { MaxTokens = 1024 } });
        var chunks = await Chunks(rag);

        Assert.True(chunks.Count(c => c.Text.Contains("Scanned", StringComparison.Ordinal)) > 1);
        Assert.All(chunks.Where(c => c.Text.Contains("Scanned", StringComparison.Ordinal)), c => Assert.True(Tokens(rag, c.Text) <= 64));
    }

    [Theory]
    [InlineData(Chunkers.FixedSize)]
    [InlineData(Chunkers.StructureAware)]
    public async Task The_configured_chunker_chunks_ocr_text_too(string chunker)
    {
        var rag = Pipeline(new InMemoryVectorStore(), MixedPdf(), ocrChunkMaxTokens: 64, configure: o => o.Chunking = o.Chunking with { Chunker = chunker });
        await Ingest(rag);

        var scanned = (await Chunks(rag)).Where(c => c.Text.Contains("Scanned", StringComparison.Ordinal)).ToList();
        Assert.True(scanned.Count > 1);
        Assert.All(scanned, c => Assert.True(Tokens(rag, c.Text) <= 64));
    }

    [Fact]
    public async Task Changing_ChunkMaxTokens_rereads_documents_with_ocr_text()
    {
        var store = new InMemoryVectorStore();
        await Ingest(Pipeline(store, MixedPdf(), ocrChunkMaxTokens: null));

        Assert.Equal(IngestStatus.Updated, (await Ingest(Pipeline(store, MixedPdf(), ocrChunkMaxTokens: 64))).Status);
        Assert.Equal(IngestStatus.Unchanged, (await Ingest(Pipeline(store, MixedPdf(), ocrChunkMaxTokens: 64))).Status);
        Assert.Equal(IngestStatus.Updated, (await Ingest(Pipeline(store, MixedPdf(), ocrChunkMaxTokens: 128))).Status);
        Assert.Equal(IngestStatus.Updated, (await Ingest(Pipeline(store, MixedPdf(), ocrChunkMaxTokens: null))).Status);
    }

    [Fact]
    public async Task Changing_ChunkMaxTokens_leaves_documents_without_scanned_pages_or_images_Unchanged()
    {
        var store = new InMemoryVectorStore();
        await Ingest(Pipeline(store, Plain, ocrChunkMaxTokens: null));

        Assert.Equal(IngestStatus.Unchanged, (await Ingest(Pipeline(store, Plain, ocrChunkMaxTokens: 64))).Status);
    }

    [Fact]
    public void Unset_option_leaves_the_ocr_fingerprint_as_before()
    {
        var unset = new OcrStage(new FakeOcr("x"), null, [], new OcrOptions());
        var set = new OcrStage(new FakeOcr("x"), null, [], new OcrOptions { ChunkMaxTokens = 128 });

        Assert.DoesNotContain("chunk", unset.Fingerprint, StringComparison.Ordinal);
        Assert.NotEqual(unset.Fingerprint, set.Fingerprint);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ChunkMaxTokens_below_16_fails_validation(int value)
    {
        var options = new RagOptions { EmbeddingDimensions = 8 };
        options.Ocr.ChunkMaxTokens = value;

        var result = RagOptionsValidator.Validate(options, hasContentStore: false, appChunkers: []);

        Assert.True(result.Failed);
        Assert.Contains("Ocr.ChunkMaxTokens", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkMaxTokens_of_16_passes_validation()
    {
        var options = new RagOptions { EmbeddingDimensions = 8 };
        options.Ocr.ChunkMaxTokens = 16;

        Assert.False(RagOptionsValidator.Validate(options, hasContentStore: false, appChunkers: []).Failed);
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

    private sealed class DocumentOcr(DocumentOcrResult result) : IDocumentOcrProvider
    {
        public string Id => "doc";

        public bool CanProcess(string? contentType, string fileName) => true;

        public Task<DocumentOcrResult> ProcessAsync(ReadOnlyMemory<byte> document, DocumentReadContext context, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
