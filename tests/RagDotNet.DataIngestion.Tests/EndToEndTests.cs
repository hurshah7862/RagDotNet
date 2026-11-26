using System.Text;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Ocr;
using RagDotNet.Pipeline;

namespace RagDotNet.DataIngestion.Tests;

public sealed class EndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(Action<IRagBuilder> configure, string? chunker = null, Action<RagOptions>? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        var builder = services.AddRag(o =>
        {
            o.EmbeddingDimensions = 64;
            o.ModelCheckInterval = TimeSpan.Zero;
            if (chunker is not null) o.Chunking = o.Chunking with { Chunker = chunker };
            options?.Invoke(o);
        });
        configure(builder);
        return services.BuildServiceProvider();
    }

    private static DocumentSource Markdown(string id, string markdown) =>
        new(id, new MemoryStream(Encoding.UTF8.GetBytes(markdown)), id + ".md", "text/markdown");

    [Fact]
    public async Task Registered_reader_and_chunker_ingest_and_retrieve()
    {
        // docs/dataingestion.md, "Setup"
        await using var services = Build(
            rag => rag
                .AddDataIngestionReader(_ => new MarkdownReader(), [".md"], ["text/markdown"])
                .AddDataIngestionChunker("ms-header", "1", (_, o) => new HeaderChunker(DataIngestionChunker.CreateOptions(o))),
            chunker: "ms-header");
        await using var scope = services.CreateAsyncScope();
        var rag = scope.ServiceProvider.GetRequiredService<IRagPipeline>();

        await rag.IngestAsync(Markdown("handbook", DataIngestionReaderTests.Handbook), cancellationToken: Ct);

        var hits = await rag.RetrieveAsync("Tell HR on the first day", cancellationToken: Ct);
        var sick = Assert.Single(hits, h => h.Text.Contains("Tell HR"));
        Assert.Equal("Sick leave", sick.HeadingPath[^1]);
    }

    [Fact]
    public async Task Reader_overrides_the_builtin_for_its_extension()
    {
        var fake = new DataIngestionReaderTests.FakeReader(_ =>
        {
            var section = new IngestionDocumentSection();
            section.Elements.Add(new IngestionDocumentParagraph("from-ms"));
            var document = new IngestionDocument("d");
            document.Sections.Add(section);
            return document;
        });
        await using var services = Build(rag => rag.AddDataIngestionReader(_ => fake, [".md"]));
        await using var scope = services.CreateAsyncScope();
        var rag = scope.ServiceProvider.GetRequiredService<IRagPipeline>();

        await rag.IngestAsync(Markdown("d", "# Core would read this"), cancellationToken: Ct);

        var hit = Assert.Single(await rag.RetrieveAsync("from-ms", cancellationToken: Ct));
        Assert.Equal("from-ms", hit.Text);
    }

    [Fact]
    public async Task Mismatched_upload_goes_to_the_builtin_reader()
    {
        // notes.md sent as text/plain: the content type decides, so RagDotNet's plain-text reader reads it.
        var fake = new DataIngestionReaderTests.FakeReader(_ =>
        {
            var section = new IngestionDocumentSection();
            section.Elements.Add(new IngestionDocumentParagraph("from the fake"));
            var document = new IngestionDocument("d");
            document.Sections.Add(section);
            return document;
        });
        await using var services = Build(rag => rag.AddDataIngestionReader(_ => fake, [".md"], ["text/markdown"]));
        await using var scope = services.CreateAsyncScope();
        var rag = scope.ServiceProvider.GetRequiredService<IRagPipeline>();

        await rag.IngestAsync(
            new DocumentSource("notes", new MemoryStream(Encoding.UTF8.GetBytes("from plain text")), "notes.md", "text/plain"),
            cancellationToken: Ct);

        var hit = Assert.Single(await rag.RetrieveAsync("from plain text", cancellationToken: Ct));
        Assert.Equal("from plain text", hit.Text);
    }

    [Fact]
    public async Task Image_text_is_read_by_ocr_and_retrievable()
    {
        var fake = new DataIngestionReaderTests.FakeReader(_ =>
        {
            var section = new IngestionDocumentSection();
            section.Elements.Add(new IngestionDocumentParagraph("Quarterly report"));
            section.Elements.Add(new IngestionDocumentImage("![chart](x)")
            {
                Content = new byte[] { 1, 2, 3 },
                MediaType = "image/png",
                AlternativeText = "chart",
            });
            var document = new IngestionDocument("d");
            document.Sections.Add(section);
            return document;
        });
        await using var services = Build(rag => rag
            .AddDataIngestionReader(_ => fake, [".md"], ["text/markdown"])
            .AddOcrProvider<ChartOcr>());
        await using var scope = services.CreateAsyncScope();
        var rag = scope.ServiceProvider.GetRequiredService<IRagPipeline>();

        var result = await rag.IngestAsync(Markdown("report", "ignored"), cancellationToken: Ct);

        Assert.Empty(result.Warnings);
        var hits = await rag.RetrieveAsync("Revenue grew", cancellationToken: Ct);
        Assert.Contains(hits, h => h.Text.Contains("Revenue grew 12 percent", StringComparison.Ordinal));
    }

    private sealed class ChartOcr : IOcrProvider
    {
        public string Id => "fake-ocr/1";

        public Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult("Revenue grew 12 percent", 0.95));
    }

    [Fact]
    public async Task Ocr_chunk_size_keeps_heading_paths_for_a_heading_driven_chunker()
    {
        // Ocr.ChunkMaxTokens chunks OCR text apart from the text around it; HeaderChunker builds paths from heading
        // blocks only, so every part must still see the headings above it.
        await using var services = Build(
            rag => rag
                .AddReader<SectionWithImageReader>()
                .AddOcrProvider<LongOcr>()
                .AddDataIngestionChunker("ms-header", "1", (_, o) => new HeaderChunker(DataIngestionChunker.CreateOptions(o))),
            chunker: "ms-header",
            options: o => o.Ocr.ChunkMaxTokens = 32);
        await using var scope = services.CreateAsyncScope();
        var rag = scope.ServiceProvider.GetRequiredService<IRagPipeline>();

        await rag.IngestAsync(new DocumentSource("guide", new MemoryStream([1]), "guide.bin"), cancellationToken: Ct);

        var hits = await rag.RetrieveAsync("restart", new RagDotNet.Retrieval.RetrievalOptions { TopK = 50 }, Ct);
        Assert.True(hits.Count(h => h.Text.Contains("Scanned", StringComparison.Ordinal)) > 1);
        Assert.Contains(hits, h => h.Text.Contains("Then restart.", StringComparison.Ordinal));
        Assert.All(hits, h => Assert.Equal(["Install", "Linux"], h.HeadingPath));
    }

    private sealed class SectionWithImageReader : IDocumentReader
    {
        public bool CanRead(string? contentType, string fileName) => true;

        public Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ParsedDocument(
            [
                new DocumentBlock(BlockKind.Heading, "# Install", ["Install"]),
                new DocumentBlock(BlockKind.Heading, "## Linux", ["Install", "Linux"]),
                new DocumentBlock(BlockKind.Paragraph, "Run apt install quill.", ["Install", "Linux"]),
                new DocumentBlock(BlockKind.Paragraph, "Then restart.", ["Install", "Linux"]),
            ])
            {
                OcrCandidates = [new OcrCandidate(OcrCandidateKind.Image, 3, null, new ImageInput(new byte[] { 1 }, "image/png", 500, 500))],
            });
    }

    private sealed class LongOcr : IOcrProvider
    {
        public string Id => "long-ocr/1";

        public Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult(string.Join(" ", Enumerable.Range(1, 30).Select(i => $"Scanned sentence {i} is here.")), 0.95));
    }

    [Fact]
    public void Registration_validates_arguments()
    {
        var rag = new ServiceCollection().AddRag(o => o.EmbeddingDimensions = 64);

        Assert.Throws<ArgumentNullException>(() => rag.AddDataIngestionReader(null!, [".md"]));
        Assert.Throws<ArgumentException>(() => rag.AddDataIngestionReader(_ => new MarkdownReader(), []));
        Assert.Throws<ArgumentNullException>(() => rag.AddDataIngestionChunker("n", "1", null!));
        Assert.Throws<ArgumentException>(() => rag.AddDataIngestionChunker(" ", "1", (_, o) => new HeaderChunker(DataIngestionChunker.CreateOptions(o))));
    }
}
