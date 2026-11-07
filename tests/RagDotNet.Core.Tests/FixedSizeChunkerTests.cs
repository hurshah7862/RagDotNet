using Microsoft.ML.Tokenizers;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Readers;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class FixedSizeChunkerTests
{
    private static readonly Tokenizer Tok = TokenCounter.CreateDefault();
    private static readonly TokenCounter Counter = new(Tok);
    private static readonly FixedSizeChunker Chunker = new(Tok);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Words(int n) => string.Join(' ', Enumerable.Range(0, n).Select(i => $"w{i}"));

    private static Task<IReadOnlyList<Chunk>> Run(ChunkingOptions options, params DocumentBlock[] blocks) =>
        Chunker.ChunkAsync(new ParsedDocument(blocks), options, Ct);

    private static async Task<ParsedDocument> ParseMarkdown(string markdown)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markdown));
        return await new MarkdownReader().ReadAsync(stream, new DocumentReadContext("d.md", null), Ct);
    }

    private static int[] Ids(string text) => [.. Tok.EncodeToIds(text)];

    [Fact]
    public async Task Windows_have_at_most_MaxTokens_and_overlap_by_the_ratio()
    {
        var text = Words(300);
        var chunks = await Run(new ChunkingOptions { MaxTokens = 100, OverlapRatio = 0.2 }, new DocumentBlock(BlockKind.Paragraph, text, []));

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(Counter.Count(c.Text) <= 100, $"{Counter.Count(c.Text)} tokens"));
        for (var i = 0; i + 1 < chunks.Count; i++)
        {
            var tail = Ids(chunks[i].Text)[^20..];
            var head = Ids(chunks[i + 1].Text)[..20];
            Assert.Equal(Tok.Decode(tail).Trim(), Tok.Decode(head).Trim());
        }

        // Removing each chunk's 20 overlapping tokens gives back the text.
        var rebuilt = chunks[0].Text + string.Concat(chunks.Skip(1).Select(c => Tok.Decode(Ids(c.Text)[20..])));
        Assert.Equal(text.Split(' '), rebuilt.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.EndsWith("w299", chunks[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zero_overlap_tiles_the_text_exactly()
    {
        var text = Words(250);
        var chunks = await Run(new ChunkingOptions { MaxTokens = 64, OverlapRatio = 0 }, new DocumentBlock(BlockKind.Paragraph, text, []));

        Assert.True(chunks.Count >= 3);
        Assert.Equal(text, string.Join(' ', chunks.Select(c => c.Text)));
    }

    [Fact]
    public async Task Headings_are_left_out_and_set_the_heading_path()
    {
        var doc = await ParseMarkdown("# A\n\npara one.\n\n## B\n\npara two.\n");

        var chunks = await Chunker.ChunkAsync(doc, new ChunkingOptions { MaxTokens = 512 }, Ct);

        var chunk = Assert.Single(chunks);
        Assert.Equal("para one.\n\npara two.", chunk.Text);
        Assert.Equal(["A"], chunk.HeadingPath);
    }

    [Fact]
    public async Task Pages_cover_the_blocks_a_window_touches()
    {
        var chunks = await Run(new ChunkingOptions { MaxTokens = 512, OverlapRatio = 0 },
            new DocumentBlock(BlockKind.Paragraph, "first page text.", [], 1),
            new DocumentBlock(BlockKind.Paragraph, "second page text.", [], 2));

        var chunk = Assert.Single(chunks);
        Assert.Equal(1, chunk.PageStart);
        Assert.Equal(2, chunk.PageEnd);
    }

    [Fact] // Review Focus 1
    public async Task Document_without_body_text_gives_no_chunks()
    {
        var doc = await ParseMarkdown("# Only\n\n## Headings\n");

        Assert.Empty(await Chunker.ChunkAsync(doc, new ChunkingOptions(), Ct));
        Assert.Empty(await Run(new ChunkingOptions()));
    }

    [Fact]
    public async Task Selected_by_name_through_the_pipeline()
    {
        var options = new ChunkingOptions { Chunker = Chunkers.FixedSize, MaxTokens = 32 };
        var markdown = "# Guide\n\n" + Words(200) + "\n";
        var (rag, _, _, _, _) = Create(o => o.Chunking = options);

        var result = await rag.IngestAsync(Md("d", markdown), cancellationToken: Ct);

        Assert.Equal(IngestStatus.Created, result.Status);
        Assert.Equal((await Chunker.ChunkAsync(await ParseMarkdown(markdown), options, Ct)).Count, result.ChunkCount);
    }

    [Fact] // Review Focus 3
    public void App_chunker_named_fixed_size_fails_validation()
    {
        var result = RagOptionsValidator.Validate(new RagOptions(), hasContentStore: false, [new FixedSizeChunker(Tok)]);

        Assert.True(result.Failed);
        Assert.Contains("Chunker name 'fixed-size' is registered more than once.", result.Failures!);
    }

    [Theory]
    [InlineData(8, 0.1)]
    [InlineData(64, 0.5)]
    [InlineData(64, -0.1)]
    public async Task Invalid_options_throw(int maxTokens, double overlap) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Run(new ChunkingOptions { MaxTokens = maxTokens, OverlapRatio = overlap }, new DocumentBlock(BlockKind.Paragraph, "text.", [])));
}
