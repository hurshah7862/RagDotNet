using System.Collections.Concurrent;
using RagDotNet.Filtering;
using RagDotNet.Keyword;

namespace RagDotNet.Core.Tests.Hybrid;

public class InMemoryKeywordIndexTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoMeta = new Dictionary<string, object?>();

    private static KeywordChunk Chunk(string key, string text, string partition = "", IReadOnlyDictionary<string, object?>? meta = null) =>
        new(key, key.Split(':')[0], partition, text, meta ?? NoMeta);

    private static async Task<List<string>> Keys(IKeywordIndex index, string query, KeywordFilter? filter = null, int top = 10) =>
        (await index.SearchAsync(query, filter ?? KeywordFilter.All, top, CancellationToken.None)).Select(h => h.ChunkKey).ToList();

    [Fact]
    public void Name_is_in_memory()
    {
        Assert.Equal("in-memory", new InMemoryKeywordIndex().Name);
    }

    [Fact]
    public async Task Exact_term_ranks_its_chunk_first()
    {
        var index = new InMemoryKeywordIndex();
        await index.UpsertAsync([
            Chunk("a:0", "The server returned an error while loading the page."),
            Chunk("b:0", "If you see ERR-404 the resource is missing; check the error log."),
            Chunk("c:0", "Errors are rare when the cache is warm."),
        ], CancellationToken.None);

        var keys = await Keys(index, "ERR-404");

        Assert.Equal("b:0", keys[0]);
    }

    [Fact]
    public async Task Bm25_prefers_rarer_terms_and_shorter_chunks()
    {
        var index = new InMemoryKeywordIndex();
        var filler = string.Join(' ', Enumerable.Repeat("padding words here", 15));
        await index.UpsertAsync([
            Chunk("long:0", "zebra " + filler),
            Chunk("short:0", "zebra lives in africa"),
            Chunk("other:0", "common words only " + filler),
        ], CancellationToken.None);

        Assert.Equal(["short:0", "long:0"], await Keys(index, "zebra"));

        // A chunk matching both query terms outranks chunks matching one.
        var hits = await index.SearchAsync("zebra padding", KeywordFilter.All, 10, CancellationToken.None);
        Assert.Equal("long:0", hits[0].ChunkKey);
        Assert.All(hits, h => Assert.True(h.Score > 0));
    }

    [Fact]
    public async Task Filter_partitions_and_common_area()
    {
        var index = new InMemoryKeywordIndex();
        await index.UpsertAsync([
            Chunk("a|d:0", "shared term", "a"),
            Chunk("b|d:0", "shared term", "b"),
            Chunk("d:0", "shared term", ""),
        ], CancellationToken.None);

        var own = await Keys(index, "term", new KeywordFilter(["a", ""], false, null));
        Assert.Equal(["a|d:0", "d:0"], own.Order(StringComparer.Ordinal));

        var noCommon = await Keys(index, "term", new KeywordFilter(null, true, null));
        Assert.Equal(["a|d:0", "b|d:0"], noCommon.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Metadata_filter_and_missing_values()
    {
        var index = new InMemoryKeywordIndex();
        await index.UpsertAsync([
            Chunk("x:0", "alpha", meta: new Dictionary<string, object?> { ["category"] = "x", ["year"] = 2024L, ["tags"] = new[] { "red" } }),
            Chunk("y:0", "alpha", meta: new Dictionary<string, object?> { ["category"] = "y", ["year"] = 2023L }),
            Chunk("none:0", "alpha"),
            Chunk("nullcat:0", "alpha", meta: new Dictionary<string, object?> { ["category"] = null }),
        ], CancellationToken.None);

        Assert.Equal(["x:0"], await Keys(index, "alpha", new KeywordFilter(null, false, RagFilter.Eq("category", "x"))));
        Assert.Equal(["x:0", "y:0"], (await Keys(index, "alpha", new KeywordFilter(null, false, RagFilter.In("year", [2023L, 2024L])))).Order(StringComparer.Ordinal));
        Assert.Equal(["x:0"], await Keys(index, "alpha", new KeywordFilter(null, false, RagFilter.Contains("tags", "red"))));
        Assert.Equal(["y:0"], await Keys(index, "alpha", new KeywordFilter(null, false, RagFilter.Eq("DocumentId", "y"))));
        Assert.Equal(["x:0"], await Keys(index, "alpha", new KeywordFilter(null, false, RagFilter.Eq("category", "x") & RagFilter.Eq("year", 2024L))));
    }

    [Fact]
    public async Task Delete_by_keys_removes_only_those_chunks()
    {
        var index = new InMemoryKeywordIndex();
        await index.UpsertAsync([Chunk("a:0", "word"), Chunk("a:1", "word"), Chunk("b:0", "word")], CancellationToken.None);

        await index.DeleteAsync(["a:0", "a:1", "missing:9"], CancellationToken.None);

        Assert.Equal(["b:0"], await Keys(index, "word"));
        Assert.Equal(1, await index.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_same_key_replaces_text()
    {
        var index = new InMemoryKeywordIndex();
        await index.UpsertAsync([Chunk("a:0", "old words")], CancellationToken.None);
        await index.UpsertAsync([Chunk("a:0", "new words")], CancellationToken.None);

        Assert.Empty(await Keys(index, "old"));
        Assert.Equal(["a:0"], await Keys(index, "new"));
        Assert.Equal(1, await index.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Count_and_rebuild()
    {
        var index = new InMemoryKeywordIndex();
        await index.UpsertAsync([Chunk("old:0", "stale"), Chunk("old:1", "stale")], CancellationToken.None);

        await index.RebuildAsync(Source(Chunk("n:0", "fresh"), Chunk("n:1", "fresh"), Chunk("n:2", "fresh")), CancellationToken.None);

        Assert.Equal(3, await index.CountAsync(CancellationToken.None));
        Assert.Empty(await Keys(index, "stale"));
        Assert.Equal(3, (await Keys(index, "fresh")).Count);
    }

    [Fact]
    public async Task Stop_word_query_returns_nothing()
    {
        var index = new InMemoryKeywordIndex();
        await index.UpsertAsync([Chunk("a:0", "the cat")], CancellationToken.None);

        Assert.Empty(await Keys(index, "the ?"));
    }

    [Fact]
    public async Task Concurrent_upserts_and_searches_do_not_throw()
    {
        var index = new InMemoryKeywordIndex();
        var written = new ConcurrentDictionary<string, bool>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var key = $"w{w}:{i}";
                await index.UpsertAsync([Chunk(key, $"common token{i % 7} writer{w}")], CancellationToken.None);
                written[key] = true;
                if (i % 10 == 9)
                {
                    var gone = $"w{w}:{i - 5}";
                    await index.DeleteAsync([gone], CancellationToken.None);
                    written.TryRemove(gone, out _);
                }
            }
        }));
        var seen = new ConcurrentBag<string>();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                foreach (var hit in await index.SearchAsync("common token3", KeywordFilter.All, 20, CancellationToken.None))
                {
                    Assert.True(double.IsFinite(hit.Score));
                    seen.Add(hit.ChunkKey);
                }
        }));

        await Task.WhenAll(writers.Concat(readers));

        Assert.Equal(written.Count, await index.CountAsync(CancellationToken.None));
        Assert.All(seen, k => Assert.Matches(@"^w[0-3]:\d+$", k));
    }

    private static async IAsyncEnumerable<KeywordChunk> Source(params KeywordChunk[] chunks)
    {
        foreach (var c in chunks)
        {
            await Task.Yield();
            yield return c;
        }
    }
}
