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

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var v in values)
            {
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
