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
using RagDotNet.Pipeline;

namespace RagDotNet.DataIngestion.Tests;

public sealed class EndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(Action<IRagBuilder> configure, string? chunker = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        var builder = services.AddRag(o =>
        {
            o.EmbeddingDimensions = 64;
            o.ModelCheckInterval = TimeSpan.Zero;
            if (chunker is not null) o.Chunking = o.Chunking with { Chunker = chunker };
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
    public void Registration_validates_arguments()
    {
        var rag = new ServiceCollection().AddRag(o => o.EmbeddingDimensions = 64);

        Assert.Throws<ArgumentNullException>(() => rag.AddDataIngestionReader(null!, [".md"]));
        Assert.Throws<ArgumentException>(() => rag.AddDataIngestionReader(_ => new MarkdownReader(), []));
        Assert.Throws<ArgumentNullException>(() => rag.AddDataIngestionChunker("n", "1", null!));
        Assert.Throws<ArgumentException>(() => rag.AddDataIngestionChunker(" ", "1", (_, o) => new HeaderChunker(DataIngestionChunker.CreateOptions(o))));
    }
}
