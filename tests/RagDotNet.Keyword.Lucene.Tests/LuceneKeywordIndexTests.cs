using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Filtering;
using RagDotNet.Keyword;
using RagDotNet.Keyword.Lucene;
using RagDotNet.Retrieval;

namespace RagDotNet.Keyword.Lucene.Tests;

public sealed class LuceneKeywordIndexTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyDictionary<string, object?> NoMeta = new Dictionary<string, object?>();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ragdotnet-lucene-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LuceneKeywordIndex Open() => new(new LuceneKeywordIndexOptions { Directory = _dir });

    private static KeywordChunk Chunk(string key, string text, string partition = "", IReadOnlyDictionary<string, object?>? meta = null) =>
        new(key, key.Split(':')[0].Split('|')[^1], partition, text, meta ?? NoMeta);

    private static async Task<List<string>> Keys(IKeywordIndex index, string query, KeywordFilter? filter = null, int top = 50) =>
        (await index.SearchAsync(query, filter ?? KeywordFilter.All, top, Ct)).Select(h => h.ChunkKey).ToList();

    private static async IAsyncEnumerable<KeywordChunk> Source(params KeywordChunk[] chunks)
    {
        foreach (var c in chunks)
        {
            await Task.Yield();
            yield return c;
        }
    }

    [Fact]
    public async Task Exact_term_and_bm25_ranking()
    {
        using var index = Open();
        Assert.Equal("lucene", index.Name);
        var filler = string.Join(' ', Enumerable.Repeat("padding words here", 15));
        await index.UpsertAsync([
            Chunk("a:0", "The server returned an error while loading the page."),
            Chunk("b:0", "If you see ERR-404 the resource is missing; check the error log."),
            Chunk("long:0", "zebra " + filler),
            Chunk("short:0", "zebra lives in africa"),
        ], Ct);

        Assert.Equal("b:0", (await Keys(index, "ERR-404"))[0]);
        Assert.Equal(["short:0", "long:0"], await Keys(index, "zebra"));
        Assert.All(await index.SearchAsync("zebra", KeywordFilter.All, 10, Ct), h => Assert.True(h.Score > 0));
    }

    [Fact]
    public async Task Ranks_with_bm25_term_saturation()
    {
        // Classic TF-IDF (Lucene 4.8's default) puts "repeated" first: sqrt(100) outweighs a second matching term.
        // BM25 saturates term frequency, so the chunk that matches both terms wins.
        using var index = Open();
        var filler = string.Join(' ', Enumerable.Range(0, 98).Select(i => "filler" + i));
        await index.UpsertAsync([
            Chunk("repeated:0", string.Join(' ', Enumerable.Repeat("apple", 100))),
            Chunk("both:0", "apple banana " + filler),
            Chunk("other:0", "cherry date"),
        ], Ct);

        Assert.Equal(["both:0", "repeated:0"], await Keys(index, "apple banana"));
    }

    [Fact]
    public async Task Filters_do_not_change_scores()
    {
        // Identical text in a small and a large partition must score the same: the filter only selects.
        using var index = Open();
        var chunks = new List<KeywordChunk> { Chunk("small|a:0", "quarterly report", "p:small") };
        chunks.AddRange(Enumerable.Range(0, 20).Select(i => Chunk($"large|d{i}:0", i == 0 ? "quarterly report" : "unrelated text " + i, "p:large")));
        await index.UpsertAsync(chunks, Ct);

        var hits = await index.SearchAsync("quarterly", new KeywordFilter(["p:small", "p:large"], false, null), 10, Ct);

        Assert.Equal(2, hits.Count);
        Assert.Equal(hits[0].Score, hits[1].Score, 6);
    }

    [Fact]
    public async Task Persists_across_restarts()
    {
        using (var index = Open())
            await index.UpsertAsync([Chunk("a:0", "persisted zorblax"), Chunk("b:0", "other text")], Ct);

        using var reopened = Open();

        Assert.Equal(2, await reopened.CountAsync(Ct));
        Assert.Equal(["a:0"], await Keys(reopened, "zorblax"));
    }

    [Fact]
    public async Task Delete_by_keys_and_upsert_replaces()
    {
        using var index = Open();
        await index.UpsertAsync([Chunk("a:0", "word old"), Chunk("a:1", "word"), Chunk("b:0", "word")], Ct);
        await index.UpsertAsync([Chunk("b:0", "word new")], Ct);

        await index.DeleteAsync(["a:0", "a:1", "missing:9"], Ct);

        Assert.Equal(["b:0"], await Keys(index, "word"));
        Assert.Empty(await Keys(index, "old"));
        Assert.Equal(["b:0"], await Keys(index, "new"));
        Assert.Equal(1, await index.CountAsync(Ct));
    }

    [Fact]
    public async Task Rebuild_replaces_all()
    {
        using var index = Open();
        await index.UpsertAsync([Chunk("old:0", "stale"), Chunk("old:1", "stale")], Ct);

        await index.RebuildAsync(Source(Chunk("n:0", "fresh"), Chunk("n:1", "fresh"), Chunk("n:2", "fresh")), Ct);

        Assert.Equal(3, await index.CountAsync(Ct));
        Assert.Empty(await Keys(index, "stale"));
        Assert.Equal(3, (await Keys(index, "fresh")).Count);
    }

    [Fact]
    public async Task Partition_rules()
    {
        using var index = Open();
        await index.UpsertAsync([
            Chunk("a|d:0", "shared term", "a"),
            Chunk("b|d:0", "shared term", "b"),
            Chunk("d:0", "shared term", ""),
        ], Ct);

        Assert.Equal(["a|d:0", "d:0"], (await Keys(index, "term", new KeywordFilter(["a", ""], false, null))).Order(StringComparer.Ordinal));
        Assert.Equal(["a|d:0"], await Keys(index, "term", new KeywordFilter(["a"], false, null)));
        Assert.Equal(["a|d:0", "b|d:0"], (await Keys(index, "term", new KeywordFilter(null, true, null))).Order(StringComparer.Ordinal));
        Assert.Equal(3, (await Keys(index, "term")).Count);
    }

    [Fact]
    public async Task Stop_word_and_punctuation_queries_return_nothing()
    {
        using var index = Open();
        await index.UpsertAsync([Chunk("a:0", "the cat")], Ct);

        Assert.Empty(await Keys(index, "the ?"));
        Assert.Empty(await Keys(index, ""));
    }

    [Fact]
    public async Task Long_query_does_not_throw()
    {
        using var index = Open();
        await index.UpsertAsync([Chunk("a:0", "word17 appears here"), Chunk("b:0", "nothing")], Ct);
        var query = string.Join(' ', Enumerable.Range(0, 2000).Select(i => "word" + i));

        var keys = await Keys(index, query);

        Assert.Equal(["a:0"], keys);
    }

    [Fact]
    public async Task Query_with_Lucene_syntax_characters_is_plain_text()
    {
        using var index = Open();
        await index.UpsertAsync([Chunk("a:0", "title foo and bar"), Chunk("b:0", "nothing")], Ct);

        var keys = await Keys(index, "title:(foo AND bar)* -nothing ~2 \"unclosed");

        Assert.Contains("a:0", keys);
        Assert.Contains("b:0", keys); // "nothing" is a plain term, not a negation
    }

    [Fact]
    public async Task Second_instance_on_the_same_directory_fails_clearly()
    {
        using var first = Open();

        var ex = Assert.Throws<InvalidOperationException>(() => Open());

        Assert.Contains(_dir, ex.Message);
        Assert.Contains("in use", ex.Message);
    }

    [Fact]
    public void Missing_directory_option_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new LuceneKeywordIndex(new LuceneKeywordIndexOptions()));
    }

    [Fact]
    public async Task Metadata_filters_match_the_vector_store()
    {
        void Schema(RagOptions o)
        {
            o.Metadata.Add<string>("category");
            o.Metadata.Add<int>("year");
            o.Metadata.Add<long>("size");
            o.Metadata.Add<bool>("active");
            o.Metadata.Add<DateTimeOffset>("published");
            o.Metadata.Add<string[]>("tags");
        }

        var day = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2));
        var docs = new (string Id, Dictionary<string, object?> Meta)[]
        {
            ("d1", new() { ["category"] = "x", ["year"] = 2024, ["size"] = 5L, ["active"] = true, ["published"] = day, ["tags"] = new[] { "red", "green" } }),
            ("d2", new() { ["category"] = "y", ["year"] = 2023, ["size"] = 7L, ["active"] = false, ["published"] = day.AddDays(1), ["tags"] = new[] { "blue" } }),
            ("d3", new() { ["category"] = "x", ["year"] = 2023, ["active"] = true }),
            ("d4", new() { ["tags"] = new[] { "red" } }),
            ("d5", new()),
            ("d6", new() { ["category"] = "z", ["year"] = 2024, ["size"] = 5L, ["active"] = false, ["published"] = day.ToOffset(TimeSpan.Zero) }),
        };

        using var lucene = Open();
        var store = new InMemoryVectorStore();
        var rag = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(store, "test", 64)
            .UseKeywordIndex(lucene)
            .Configure(Schema)
            .Build();
        foreach (var (id, meta) in docs)
        {
            var source = new DocumentSource(id, new MemoryStream(System.Text.Encoding.UTF8.GetBytes("common text " + id)), id + ".txt", "text/plain");
            foreach (var (k, v) in meta) source.Metadata[k] = v;
            await rag.IngestAsync(source, cancellationToken: Ct);
        }

        // The keyword filter holds values already converted to the declared types, as RagDotNet builds it.
        var filters = new RagFilter[]
        {
            RagFilter.Eq("category", "x"),
            RagFilter.Eq("year", 2024),
            RagFilter.Eq("size", 5L),
            RagFilter.Eq("active", true),
            RagFilter.Eq("active", false),
            RagFilter.Eq("published", day),
            RagFilter.In("year", [2023, 2022]),
            RagFilter.Contains("tags", "red"),
            RagFilter.ContainsAny("tags", ["green", "blue"]),
            RagFilter.Eq("category", "x") & RagFilter.Eq("active", true),
            RagFilter.Eq("year", 2023) | RagFilter.Contains("tags", "red"),
            RagFilter.Eq("DocumentId", "d4"),
            RagFilter.Eq("category", "missing-value"),
        };

        foreach (var filter in filters)
        {
            var fromLucene = (await lucene.SearchAsync("common", new KeywordFilter(null, false, filter), 50, Ct))
                .Select(h => h.ChunkKey.Split(':')[0]).Order(StringComparer.Ordinal).ToList();
            var fromStore = (await rag.RetrieveAsync("common text", new RetrievalOptions { TopK = 50, Filter = filter, Hybrid = HybridMode.Off }, Ct))
                .Select(c => c.DocumentId).Order(StringComparer.Ordinal).ToList();
            Assert.True(fromStore.SequenceEqual(fromLucene), $"{filter}: store [{string.Join(",", fromStore)}] lucene [{string.Join(",", fromLucene)}]");
        }
    }

    [Fact]
    public async Task Pipeline_with_AddLuceneKeywordIndex_finds_exact_terms()
    {
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(new InMemoryVectorStore(), "test", 64)
            .Configure(o => o.HybridCandidates = 2)
            .AddLuceneKeywordIndex(o => o.Directory = _dir);
        var rag = builder.Build();
        foreach (var (word, i) in FakeEmbeddingGenerator.Colliding("err-404", 64).Take(3).Select((w, i) => (w, i)))
            await rag.IngestAsync(Text($"filler{i}", word), cancellationToken: Ct);
        await rag.IngestAsync(Text("target", "ERR-404 means the page is missing from the server today"), cancellationToken: Ct);

        var results = await rag.RetrieveAsync("ERR-404", new RetrievalOptions { TopK = 2 }, Ct);

        Assert.Contains(results, c => c.DocumentId == "target");
        Assert.All(results, c => Assert.Equal(ScoreKind.Fused, c.ScoreKind));
    }

    private static DocumentSource Text(string id, string text) =>
        new(id, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), id + ".txt", "text/plain");

    [Fact]
    public async Task Di_registers_validates_and_disposes_the_index()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddRag(o => o.EmbeddingDimensions = 64).AddLuceneKeywordIndex(o => o.Directory = _dir);
        LuceneKeywordIndex index;
        using (var provider = services.BuildServiceProvider())
        {
            index = Assert.IsType<LuceneKeywordIndex>(provider.GetRequiredService<IKeywordIndex>());
            Assert.Single(provider.GetServices<IKeywordIndex>());
            await index.UpsertAsync([Chunk("a:0", "zorblax")], Ct);
        }

        // Disposed with the container: the directory's write lock is released.
        using var reopened = Open();
        Assert.Equal(1, await reopened.CountAsync(Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.CountAsync(Ct));
    }

    [Fact]
    public async Task Di_fails_at_start_without_a_directory_or_when_it_is_locked()
    {
        var missing = Host.CreateApplicationBuilder();
        missing.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        missing.Services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        missing.Services.AddRag(o => o.EmbeddingDimensions = 64).AddLuceneKeywordIndex(_ => { });
        using (var host = missing.Build())
            Assert.Contains("Directory", (await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct))).Message);

        using var holder = Open();
        var locked = Host.CreateApplicationBuilder();
        locked.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        locked.Services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        locked.Services.AddRag(o => o.EmbeddingDimensions = 64).AddLuceneKeywordIndex(o => o.Directory = _dir);
        using (var host = locked.Build())
            Assert.Contains(_dir, (await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct))).Message);
    }
}
