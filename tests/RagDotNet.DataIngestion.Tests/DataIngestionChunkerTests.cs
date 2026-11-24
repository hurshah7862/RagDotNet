using System.Text;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using RagDotNet.Chunking;
using RagDotNet.Documents;
using CoreMarkdownReader = RagDotNet.Readers.MarkdownReader;

namespace RagDotNet.DataIngestion.Tests;

public sealed class DataIngestionChunkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DataIngestionChunker Header = new("ms-header", "1", o => new HeaderChunker(DataIngestionChunker.CreateOptions(o)));

    private static Task<ParsedDocument> ParseAsync(string markdown) =>
        new CoreMarkdownReader().ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(markdown)), new DocumentReadContext("doc.md", "text/markdown"), Ct);

    [Fact]
    public async Task Header_chunker_chunks_carry_the_heading_path_without_the_prefix()
    {
        var chunks = await Header.ChunkAsync(await ParseAsync(DataIngestionReaderTests.Handbook), new ChunkingOptions(), Ct);

        var sick = Assert.Single(chunks, c => c.Text.Contains("Tell HR"));
        Assert.Equal(["Handbook", "Leave", "Sick leave"], sick.HeadingPath);
        Assert.False(sick.Text.StartsWith('#'), sick.Text);
    }

    [Fact]
    public async Task Heading_path_survives_hashes_in_headings()
    {
        var parsed = await ParseAsync("# C# ## guide\n\nFirst body.\n\n## Part #2\n\nSecond body.\n");

        var chunks = await Header.ChunkAsync(parsed, new ChunkingOptions(), Ct);

        Assert.Equal(["C# ## guide", "Part #2"], Assert.Single(chunks, c => c.Text.Contains("Second body")).HeadingPath);
        Assert.Equal(["C# ## guide"], Assert.Single(chunks, c => c.Text.Contains("First body")).HeadingPath);
    }

    [Fact]
    public async Task Context_prefix_is_removed_with_either_line_ending()
    {
        // HeaderChunker ends its context line with Environment.NewLine, which is \r\n on Windows.
        var chunker = new DataIngestionChunker("fake", "1", _ => new FixedChunker("# A\r\nbody one", "# A\nbody two"));

        var chunks = await chunker.ChunkAsync(await ParseAsync("# A\n\nbody\n"), new ChunkingOptions(), Ct);

        Assert.Equal(["body one", "body two"], chunks.Select(c => c.Text));
        Assert.All(chunks, c => Assert.Equal(["A"], c.HeadingPath));
    }

    private sealed class FixedChunker(params string[] contents) : IngestionChunker<string>
    {
        public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
            IngestionDocument document, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var content in contents) yield return new IngestionChunk<string>(content, document, "# A");
        }
    }

    [Fact]
    public async Task Unknown_context_gives_an_empty_path()
    {
        var tokens = new DataIngestionChunker("ms-tokens", "1", o => new DocumentTokenChunker(DataIngestionChunker.CreateOptions(o)));

        var chunks = await tokens.ChunkAsync(await ParseAsync(DataIngestionReaderTests.Handbook), new ChunkingOptions(), Ct);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            Assert.Empty(c.HeadingPath);
            Assert.False(string.IsNullOrWhiteSpace(c.Text));
        });
    }

    [Fact]
    public async Task Chunk_size_follows_ChunkingOptions()
    {
        var parsed = await ParseAsync("# H\n\n" + string.Join(" ", Enumerable.Repeat("word", 200)) + "\n");
        var tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

        var chunks = await Header.ChunkAsync(parsed, new ChunkingOptions { MaxTokens = 50, OverlapRatio = 0 }, Ct);

        Assert.True(chunks.Count > 1, $"{chunks.Count} chunk(s)");
        Assert.All(chunks, c => Assert.InRange(tokenizer.CountTokens(c.Text), 1, 60));
    }

    [Theory]
    [InlineData(1, 0.5, 0)]
    [InlineData(10, 0.99, 9)]
    [InlineData(512, 0.1, 51)]
    public void CreateOptions_keeps_overlap_below_max(int maxTokens, double overlapRatio, int expectedOverlap)
    {
        var options = DataIngestionChunker.CreateOptions(new ChunkingOptions { MaxTokens = maxTokens, OverlapRatio = overlapRatio });

        Assert.Equal(maxTokens, options.MaxTokensPerChunk);
        Assert.Equal(expectedOverlap, options.OverlapTokens);
    }

    [Theory]
    [InlineData("", "1")]
    [InlineData(" ", "1")]
    [InlineData("name", "")]
    public void Name_and_version_are_required(string name, string version) =>
        Assert.Throws<ArgumentException>(() => new DataIngestionChunker(name, version, o => new HeaderChunker(DataIngestionChunker.CreateOptions(o))));
}
