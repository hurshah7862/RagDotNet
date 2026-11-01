using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class CustomChunkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class LineChunker(string version = "1", string name = "lines") : IChunker
    {
        public string Name => name;

        public string Version => version;

        public Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingOptions options, CancellationToken cancellationToken)
        {
            var lines = document.Blocks.SelectMany(b => b.Text.Split('\n')).Where(l => l.Trim().Length > 0).ToList();
            return Task.FromResult<IReadOnlyList<Chunk>>(lines.Select((l, i) => new Chunk(i, l, [], null, null, 0)).ToList());
        }
    }

    private sealed class ScriptedChunker(string name, Func<IReadOnlyList<Chunk>?> output) : IChunker
    {
        public string Name => name;

        public string Version => "1";

        public Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(output()!);
    }

    private sealed class DiLineChunker : IChunker
    {
        private readonly LineChunker _inner = new(name: "diline");

        public string Name => _inner.Name;

        public string Version => _inner.Version;

        public Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingOptions options, CancellationToken cancellationToken) =>
            _inner.ChunkAsync(document, options, cancellationToken);
    }

    [Fact]
    public async Task Custom_chunker_selected_by_default_option()
    {
        var (rag, _, _, _, _) = Create(o => o.Chunking = new() { Chunker = "lines" }, b => b.AddChunker(new LineChunker()));
        var r = await rag.IngestAsync(Md("d", "one\n\ntwo\n\nthree"), cancellationToken: Ct);
        Assert.Equal(3, r.ChunkCount);
    }

    [Fact]
    public async Task Custom_chunker_selected_by_content_type()
    {
        var (rag, _, _, _, _) = Create(
            o => o.ChunkingByContentType["text/markdown"] = new() { Chunker = "lines" }, b => b.AddChunker(new LineChunker()));
        var r = await rag.IngestAsync(Md("d", "one\n\ntwo\n\nthree"), cancellationToken: Ct);
        Assert.Equal(3, r.ChunkCount);
    }

    [Fact]
    public async Task Per_call_chunker_overrides_and_unknown_name_throws_before_writing()
    {
        var (rag, b, _, _, _) = Create(builder: x => x.AddChunker(new LineChunker()));
        var admin = b.BuildAdmin();
        var r = await rag.IngestAsync(Md("d", "one\n\ntwo\n\nthree"),
            new IngestOptions { Chunking = new() { Chunker = "lines" } }, cancellationToken: Ct);
        Assert.Equal(3, r.ChunkCount);

        var before = await admin.GetStatsAsync(Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => rag.IngestAsync(Md("e", "x"),
            new IngestOptions { Chunking = new() { Chunker = "nope" } }, cancellationToken: Ct));
        var after = await admin.GetStatsAsync(Ct);
        Assert.Equal(before.Partitions.Sum(p => p.DocumentCount), after.Partitions.Sum(p => p.DocumentCount));
    }

    [Fact]
    public async Task Bumping_chunker_version_rechunks_on_next_ingest()
    {
        var embedder = new FakeEmbeddingGenerator(64);
        var store = new InMemoryVectorStore();
        IRagPipeline Build(string version) => new RagPipelineBuilder()
            .UseEmbeddingGenerator(embedder).UseVectorStore(store, "test", 64)
            .Configure(o => o.Chunking = new() { Chunker = "lines" })
            .AddChunker(new LineChunker(version)).Build();

        var first = await Build("1").IngestAsync(Md("d", "one\n\ntwo"), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Created, first.Status);
        var same = await Build("1").IngestAsync(Md("d", "one\n\ntwo"), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Unchanged, same.Status);
        var bumped = await Build("2").IngestAsync(Md("d", "one\n\ntwo"), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Updated, bumped.Status);
    }

    [Fact]
    public async Task Output_is_normalized_blank_dropped_oversized_split_renumbered()
    {
        var big = string.Join(' ', Enumerable.Range(0, 600).Select(i => "word" + i));
        var chunker = new ScriptedChunker("scripted", () =>
        [
            new Chunk(5, "a", ["H"], 1, 2, 999),
            new Chunk(5, "   ", [], null, null, 1),
            new Chunk(9, big, ["H", "Sub"], 3, 4, 1),
        ]);
        var (rag, _, _, _, _) = Create(o => o.Chunking = new() { Chunker = "scripted", MaxTokens = 512 }, b => b.AddChunker(chunker));
        var r = await rag.IngestAsync(Md("d", "x"), cancellationToken: Ct);
        var got = await rag.RetrieveAsync("word", new RetrievalOptions { TopK = 50 }, Ct);

        Assert.Equal(r.ChunkCount, got.Count);
        Assert.True(got.Count >= 3);
        Assert.Equal(Enumerable.Range(0, got.Count), got.Select(c => c.ChunkIndex).Order());
        Assert.DoesNotContain(got, c => string.IsNullOrWhiteSpace(c.Text));
        var counter = new TokenCounter(TokenCounter.CreateDefault());
        Assert.All(got, c => Assert.True(counter.Count(c.Text) <= 512));
        var pieces = got.Where(c => c.Text != "a").ToList();
        Assert.All(pieces, c =>
        {
            Assert.Equal(["H", "Sub"], c.HeadingPath);
            Assert.Equal(3, c.PageStart);
            Assert.Equal(4, c.PageEnd);
        });
    }

    [Fact]
    public async Task Null_heading_path_from_a_chunker_is_treated_as_empty()
    {
        var chunker = new ScriptedChunker("noheadings", () => [new Chunk(0, "alpha text", null!, null, null, 0)]);
        var (rag, _, _, _, _) = Create(o => o.Chunking = new() { Chunker = "noheadings" }, b => b.AddChunker(chunker));
        var r = await rag.IngestAsync(Md("d", "x"), cancellationToken: Ct);
        Assert.Equal(1, r.ChunkCount);
        Assert.Empty(Assert.Single(await rag.RetrieveAsync("alpha", cancellationToken: Ct)).HeadingPath);
    }

    [Fact]
    public async Task Chunker_returning_null_fails_naming_it()
    {
        var (rag, _, _, _, _) = Create(o => o.Chunking = new() { Chunker = "nulls" },
            b => b.AddChunker(new ScriptedChunker("nulls", () => null)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(Md("d", "x"), cancellationToken: Ct));
        Assert.Contains("'nulls'", ex.Message);
    }

    [Fact]
    public async Task Chunker_returning_a_null_element_fails_naming_it()
    {
        var (rag, _, _, _, _) = Create(o => o.Chunking = new() { Chunker = "nulls" },
            b => b.AddChunker(new ScriptedChunker("nulls", () => [null!])));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(Md("d", "x"), cancellationToken: Ct));
        Assert.Contains("'nulls'", ex.Message);
    }

    [Theory]
    [InlineData("structure-aware")]
    [InlineData("lines")]
    public void Duplicate_chunker_name_fails_at_build(string second)
    {
        var ex = Assert.Throws<OptionsValidationException>(() =>
            Create(builder: b => b.AddChunker(new LineChunker()).AddChunker(new LineChunker(name: second))));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Blank_chunker_name_or_version_fails_at_build()
    {
        Assert.Throws<OptionsValidationException>(() => Create(builder: b => b.AddChunker(new LineChunker(name: " "))));
        Assert.Throws<OptionsValidationException>(() => Create(builder: b => b.AddChunker(new LineChunker(version: ""))));
    }

    [Fact]
    public void AddChunker_rejects_null_and_frozen_builder()
    {
        var (_, b, _, _, _) = Create();
        Assert.Throws<InvalidOperationException>(() => b.AddChunker(new LineChunker()));
        Assert.Throws<ArgumentNullException>(() => new RagPipelineBuilder().AddChunker(null!));
    }

    [Fact]
    public void Unknown_default_chunker_fails_at_build_and_at_DI_startup()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Create(o => o.Chunking = new() { Chunker = "nope" }));
        Assert.Contains("'nope'", ex.Message);
        Assert.Throws<OptionsValidationException>(() =>
            Create(o => o.ChunkingByContentType["text/plain"] = new() { Chunker = "nope" }));

        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddRag(o => { o.EmbeddingDimensions = 64; o.Chunking = new() { Chunker = "nope" }; });
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RagOptions>>().Value);
    }

    [Fact]
    public async Task DI_AddChunker_registers_a_singleton_used_by_the_pipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddRag(o => { o.EmbeddingDimensions = 64; o.Chunking = new() { Chunker = "diline" }; }).AddChunker<DiLineChunker>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Same(provider.GetRequiredService<IChunker>(), provider.GetRequiredService<IChunker>());
        var r = await scope.ServiceProvider.GetRequiredService<IRagPipeline>()
            .IngestAsync(Md("d", "one\n\ntwo\n\nthree"), cancellationToken: Ct);
        Assert.Equal(3, r.ChunkCount);
    }
}
