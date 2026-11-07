using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Storage;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class SemanticChunkerTests
{
    private static readonly Tokenizer Tok = TokenCounter.CreateDefault();
    private static readonly TokenCounter Counter = new(Tok);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>"cat" → x axis, "rocket" → y axis, anything else → z axis; counts its calls.</summary>
    private sealed class TopicEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls { get; private set; }

        public List<string> Inputs { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var v in values)
            {
                Inputs.Add(v);
                float[] vector = v.Contains("cat", StringComparison.OrdinalIgnoreCase) ? [1, 0, 0]
                    : v.Contains("rocket", StringComparison.OrdinalIgnoreCase) ? [0, 1, 0]
                    : [0, 0, 1];
                result.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static string Sentences(string topic, int n, int from = 0) =>
        string.Join(' ', Enumerable.Range(from, n).Select(i => $"The {topic} number {i} is here."));

    private static DocumentBlock Para(string text, params string[] path) => new(BlockKind.Paragraph, text, path);

    private static Task<IReadOnlyList<Chunk>> Run(TopicEmbedder embedder, ChunkingOptions options, int batchSize, params DocumentBlock[] blocks) =>
        new SemanticChunker(Tok, embedder, batchSize).ChunkAsync(new ParsedDocument(blocks), options, Ct);

    private static Task<IReadOnlyList<Chunk>> Run(ChunkingOptions options, params DocumentBlock[] blocks) =>
        Run(new TopicEmbedder(), options, 32, blocks);

    [Fact]
    public async Task Breaks_where_the_topic_changes()
    {
        var chunks = await Run(new ChunkingOptions { MaxTokens = 512 }, Para(Sentences("cat", 4) + " " + Sentences("rocket", 4)));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(Sentences("cat", 4), chunks[0].Text);
        Assert.Equal(Sentences("rocket", 4), chunks[1].Text);
    }

    [Fact]
    public async Task Respects_MaxTokens_within_a_topic()
    {
        var text = Sentences("cat", 40);

        var chunks = await Run(new ChunkingOptions { MaxTokens = 64 }, Para(text));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Counter.Count(c.Text) <= 64, $"{Counter.Count(c.Text)} tokens"));
        Assert.Equal(text, string.Join(' ', chunks.Select(c => c.Text)));
    }

    [Fact]
    public async Task Section_change_is_always_a_break()
    {
        var chunks = await Run(new ChunkingOptions { MaxTokens = 512 }, Para(Sentences("cat", 3), "A"), Para(Sentences("cat", 3, 3), "B"));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(["A"], chunks[0].HeadingPath);
        Assert.Equal(["B"], chunks[1].HeadingPath);
    }

    [Fact]
    public async Task Blocks_in_one_chunk_are_joined_by_a_blank_line()
    {
        var chunks = await Run(new ChunkingOptions { MaxTokens = 512 }, Para("The cat sat.", "A"), Para("The cat slept.", "A"));

        Assert.Equal("The cat sat.\n\nThe cat slept.", Assert.Single(chunks).Text);
    }

    [Fact]
    public async Task Code_and_table_blocks_are_single_units()
    {
        // Split into sentences, the code block would break between its cat line and its rocket line.
        const string code = "cat one.\nrocket two.";
        var chunks = await Run(new ChunkingOptions { MaxTokens = 512 },
            Para(Sentences("cat", 3)), new DocumentBlock(BlockKind.Code, code, []), Para(Sentences("rocket", 3)));

        Assert.Contains(chunks, c => c.Text.Contains(code, StringComparison.Ordinal));
    }

    [Theory] // Final review: a whole code block, or text without sentence marks, was embedded at any size
    [InlineData(BlockKind.Code)]
    [InlineData(BlockKind.Paragraph)]
    public async Task Units_over_MaxTokens_are_split_before_embedding(BlockKind kind)
    {
        var text = string.Join('\n', Enumerable.Range(0, 120).Select(i => $"var cat{i} = Compute({i})"));
        var embedder = new TopicEmbedder();

        var chunks = await Run(embedder, new ChunkingOptions { MaxTokens = 64 }, 32, new DocumentBlock(kind, text, []));

        Assert.True(Counter.Count(text) > 64 * 4);
        Assert.All(embedder.Inputs, input => Assert.True(Counter.Count(input) <= 64, $"{Counter.Count(input)} tokens embedded"));
        Assert.All(chunks, c => Assert.True(Counter.Count(c.Text) <= 64));
        Assert.Equal(text.Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal),
            string.Concat(chunks.Select(c => c.Text.Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal))));
    }

    [Fact] // Review Focus 2
    public async Task Single_sentence_and_uniform_distances_do_not_throw()
    {
        Assert.Equal("The cat sat.", Assert.Single(await Run(new ChunkingOptions(), Para("The cat sat."))).Text);

        var same = string.Join(' ', Enumerable.Repeat("The cat sat on the mat.", 10));
        Assert.Single(await Run(new ChunkingOptions { MaxTokens = 512 }, Para(same)));
        var small = await Run(new ChunkingOptions { MaxTokens = 16 }, Para(same));
        Assert.True(small.Count > 1);
        Assert.All(small, c => Assert.True(Counter.Count(c.Text) <= 16));
    }

    [Fact] // Review Focus 1
    public async Task No_body_text_makes_no_embedding_call()
    {
        var embedder = new TopicEmbedder();

        var chunks = await Run(embedder, new ChunkingOptions(), 32, new DocumentBlock(BlockKind.Heading, "Only", ["Only"]));

        Assert.Empty(chunks);
        Assert.Equal(0, embedder.Calls);
    }

    [Fact]
    public async Task Embeds_in_batches()
    {
        var embedder = new TopicEmbedder();

        await Run(embedder, new ChunkingOptions(), 32, Para(Sentences("cat", 70)));

        Assert.Equal(3, embedder.Calls);
    }

    [Fact]
    public void Percentile_is_part_of_the_chunking_hash()
    {
        var chunker = new SemanticChunker(Tok, new TopicEmbedder());
        Assert.NotEqual(
            Hashing.ChunkingHash(new ChunkingOptions { BreakpointPercentile = 95 }, chunker, "cl100k_base"),
            Hashing.ChunkingHash(new ChunkingOptions { BreakpointPercentile = 90 }, chunker, "cl100k_base"));
    }

    [Fact] // Deferred minor 6: only the semantic chunker reads the percentile
    public void Percentile_does_not_change_other_chunkers_hashes()
    {
        IChunker[] others = [new StructureAwareChunker(Tok), new FixedSizeChunker(Tok)];
        Assert.All(others, chunker => Assert.Equal(
            Hashing.ChunkingHash(new ChunkingOptions { BreakpointPercentile = 95 }, chunker, "cl100k_base"),
            Hashing.ChunkingHash(new ChunkingOptions { BreakpointPercentile = 90 }, chunker, "cl100k_base")));
    }

    [Theory] // Deferred minor 7: pins the interpolation (NumPy's default "linear" method)
    [InlineData(new double[] { 4, 0, 2, 1, 3 }, 50, 2)]
    [InlineData(new double[] { 0, 0, 0, 0, 0, 0, 1 }, 95, 0.7)]
    [InlineData(new double[] { 1, 2, 3, 4 }, 25, 1.75)]
    [InlineData(new double[] { 1, 2 }, 100, 2)]
    [InlineData(new double[] { 5 }, 10, 5)]
    public void Percentile_interpolates_between_the_nearest_ranks(double[] values, double p, double expected) =>
        Assert.Equal(expected, SemanticChunker.Percentile(values, p), 12);

    [Fact]
    public void Percentile_of_no_values_is_infinite() =>
        Assert.Equal(double.PositiveInfinity, SemanticChunker.Percentile([], 95));

    /// <summary>Returns a vector whose length is the number of words, so different sentences get different lengths.</summary>
    private sealed class RaggedEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(v => new Embedding<float>(Enumerable.Repeat(1f, v.Split(' ').Length).ToArray()))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact] // Deferred minor 3
    public async Task Vectors_of_different_lengths_throw()
    {
        var chunker = new SemanticChunker(Tok, new RaggedEmbedder());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chunker.ChunkAsync(new ParsedDocument([Para("The cat sat. The black cat sat on the mat.")]), new ChunkingOptions(), Ct));

        Assert.Contains("dimensional", ex.Message, StringComparison.Ordinal);
    }

    [Fact] // Deferred minor 3
    public async Task Vectors_must_match_the_configured_dimensions()
    {
        var chunker = new SemanticChunker(Tok, new TopicEmbedder(), dimensions: 4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chunker.ChunkAsync(new ParsedDocument([Para("The cat sat.")]), new ChunkingOptions(), Ct));

        Assert.Contains("3-dimensional", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4 dimensions", ex.Message, StringComparison.Ordinal);
    }

    private sealed class Collect : IProgress<IngestProgress>
    {
        public List<IngestProgress> Reports { get; } = [];

        public void Report(IngestProgress value) => Reports.Add(value);
    }

    [Fact] // Deferred minor 5
    public async Task Semantic_ingest_reports_sentence_embedding_progress()
    {
        var (rag, _, _, _, _) = Create(o => o.Chunking = new ChunkingOptions { Chunker = Chunkers.Semantic });
        var progress = new Collect();

        await rag.IngestAsync(Md("d", "# Guide\n\n" + Sentences("cat", 5) + "\n"), progress: progress, cancellationToken: Ct);

        var chunking = progress.Reports.Where(r => r.Stage == IngestStage.Chunking).ToList();
        Assert.Contains(chunking, r => r.TotalChunks == 5 && r.ChunksEmbedded == 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    public async Task Invalid_percentile_throws(double percentile) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Run(new ChunkingOptions { BreakpointPercentile = percentile }, Para("The cat sat.")));

    [Fact]
    public async Task Selected_by_name_through_the_pipeline()
    {
        var (rag, _, _, _, _) = Create(o => o.Chunking = new ChunkingOptions { Chunker = Chunkers.Semantic });

        var result = await rag.IngestAsync(Md("d", "# Guide\n\n" + Sentences("cat", 5) + "\n"), cancellationToken: Ct);

        Assert.Equal(IngestStatus.Created, result.Status);
        Assert.True(result.ChunkCount >= 1);
    }

    [Fact] // Review Focus 3
    public void App_chunker_named_semantic_fails_validation()
    {
        var result = RagOptionsValidator.Validate(new RagOptions(), hasContentStore: false, [new SemanticChunker(Tok, new TopicEmbedder())]);

        Assert.True(result.Failed);
        Assert.Contains("Chunker name 'semantic' is registered more than once.", result.Failures!);
    }
}
