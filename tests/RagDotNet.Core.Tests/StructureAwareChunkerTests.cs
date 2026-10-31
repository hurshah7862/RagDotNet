using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;
using RagDotNet.Chunking;
using RagDotNet.Documents;

namespace RagDotNet.Core.Tests;

public class StructureAwareChunkerTests
{
    private static readonly Tokenizer Tok = TokenCounter.CreateDefault();
    private static readonly TokenCounter Counter = new(Tok);
    private static readonly StructureAwareChunker Chunker = new(Tok);

    private static DocumentBlock Para(string text, string[] path, int? page = null) =>
        new(BlockKind.Paragraph, text, path, page);

    private static string TextOfAbout(int tokens, string tag)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (Counter.Count(sb.ToString()) < tokens)
            sb.Append($"Sentence {tag} {i++} is here. ");
        return sb.ToString().Trim();
    }

    private static Task<IReadOnlyList<Chunk>> Run(ChunkingOptions? options, params DocumentBlock[] blocks) =>
        Chunker.ChunkAsync(new ParsedDocument(blocks), options ?? new ChunkingOptions(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Never_crosses_a_heading_boundary()
    {
        var chunks = await Run(null,
            new DocumentBlock(BlockKind.Heading, "A", ["A"]),
            Para(TextOfAbout(50, "a"), ["A"]),
            new DocumentBlock(BlockKind.Heading, "B", ["B"]),
            Para(TextOfAbout(50, "b"), ["B"]));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(["A"], chunks[0].HeadingPath);
        Assert.Equal(["B"], chunks[1].HeadingPath);
        Assert.DoesNotContain("Sentence b", chunks[0].Text);
    }

    [Fact]
    public async Task Merges_small_blocks_under_the_same_heading()
    {
        var blocks = Enumerable.Range(0, 5).Select(i => Para(TextOfAbout(20, $"p{i}"), ["H"])).ToArray();
        var chunks = await Run(null, blocks);
        var chunk = Assert.Single(chunks);
        Assert.Contains("\n\n", chunk.Text);
    }

    [Fact]
    public async Task Splits_long_sections_at_paragraphs_then_sentences()
    {
        var paragraph = TextOfAbout(1500, "long");
        var sentences = Regex.Split(paragraph, @"(?<=[.!?。！？])\s+");
        var chunks = await Run(null, Para(paragraph, ["H"]));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 512));
        foreach (var s in sentences)
            Assert.Contains(chunks, c => c.Text.Contains(s));
    }

    [Fact]
    public async Task Keeps_code_block_whole_when_it_fits()
    {
        var code = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"var value{i} = Compute({i}) + {i * 7};"));
        Assert.InRange(Counter.Count(code), 250, 512);
        var chunks = await Run(null, new DocumentBlock(BlockKind.Code, code, ["H"]));
        var chunk = Assert.Single(chunks);
        Assert.Equal(code, chunk.Text);
    }

    [Fact]
    public async Task Splits_oversized_code_block_by_lines()
    {
        var lines = Enumerable.Range(0, 200).Select(i => $"var value{i} = Compute({i}) + {i * 7};").ToArray();
        var code = string.Join("\n", lines);
        Assert.True(Counter.Count(code) > 1000);
        var chunks = await Run(null, new DocumentBlock(BlockKind.Code, code, ["H"]));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 512));
        var set = lines.ToHashSet();
        Assert.All(chunks, c => Assert.All(c.Text.Split('\n'), l => Assert.Contains(l, set)));
    }

    [Fact]
    public async Task Code_overlap_carries_whole_lines_even_when_lines_end_in_sentence_punctuation()
    {
        var lines = Enumerable.Range(0, 120).Select(i => $"// Step {i} done. Next!").ToArray();
        var options = new ChunkingOptions { MaxTokens = 64, OverlapRatio = 0.25 };
        var chunks = await Run(options, new DocumentBlock(BlockKind.Code, string.Join("\n", lines), ["H"]));

        Assert.True(chunks.Count > 2);
        var set = lines.ToHashSet();
        Assert.All(chunks, c => Assert.All(c.Text.Split('\n'), l => Assert.Contains(l, set)));
        Assert.All(chunks, c => Assert.True(Counter.Count(c.Text) <= 64));
        // Overlap really happened: the next chunk starts with trailing whole lines of the previous chunk.
        Assert.Contains(chunks[1].Text.Split('\n').First(), chunks[0].Text.Split('\n'));    }

    [Fact]
    public async Task Table_split_across_chunks_keeps_whole_rows()
    {
        var rows = Enumerable.Range(0, 120).Select(i => $"| e.g. item {i}. | value {i}! |").ToArray();
        var options = new ChunkingOptions { MaxTokens = 64, OverlapRatio = 0.25 };
        var chunks = await Run(options, new DocumentBlock(BlockKind.Table, string.Join("\n", rows), ["H"]));

        Assert.True(chunks.Count > 2);
        var set = rows.ToHashSet();
        Assert.All(chunks, c => Assert.All(c.Text.Split('\n'), l => Assert.Contains(l, set)));
    }

    [Fact]
    public async Task Splits_code_block_with_crlf_line_endings()
    {
        var lines = Enumerable.Range(0, 200).Select(i => $"var value{i} = Compute({i}) + {i * 7};").ToArray();
        var chunks = await Run(null, new DocumentBlock(BlockKind.Code, string.Join("\r\n", lines), ["H"]));
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.DoesNotContain('\r', c.Text));
    }

    [Fact]
    public async Task Hard_splits_text_without_boundaries()
    {
        var cjk = string.Concat(Enumerable.Range(0, 5000).Select(i => (char)(0x4E00 + i % 2000)));
        foreach (var text in new[] { new string('x', 20000), cjk })
        {
            var chunks = await Run(null, Para(text, ["H"]));
            Assert.True(chunks.Count > 1);
            Assert.All(chunks, c => Assert.True(c.TokenCount <= 512));
            // Re-measured independently of the stored TokenCount.
            Assert.All(chunks, c => Assert.True(Counter.Count(c.Text) <= 512));
            Assert.All(chunks, c => Assert.Equal(Counter.Count(c.Text), c.TokenCount));
        }
    }

    [Fact]
    public async Task Applies_overlap_between_consecutive_chunks_in_a_section()
    {
        var text = string.Join(" ", Enumerable.Range(0, 80).Select(i => $"Alpha {i} beta."));
        var options = new ChunkingOptions { MaxTokens = 64, OverlapRatio = 0.1 };
        var chunks = await Run(options, Para(text, ["H"]));

        Assert.True(chunks.Count > 2);
        for (var i = 0; i + 1 < chunks.Count; i++)
        {
            var last = Regex.Split(chunks[i].Text, @"(?<=[.!?。！？])\s+").Last();
            Assert.StartsWith(last, chunks[i + 1].Text);
            Assert.True(Counter.Count(last) <= (int)Math.Floor(64 * 0.1));
            Assert.True(chunks[i + 1].TokenCount <= 64);
        }
    }

    [Fact]
    public async Task Indexes_are_sequential_and_page_range_spans_blocks()
    {
        var chunks = await Run(null,
            Para("First.", ["H"], 1),
            Para("Second.", ["H"], 1),
            Para("Third.", ["H"], 2),
            Para("Other section.", ["I"], 3));

        Assert.Equal(2, chunks.Count);
        Assert.Equal([0, 1], chunks.Select(c => c.Index));
        Assert.Equal(1, chunks[0].PageStart);
        Assert.Equal(2, chunks[0].PageEnd);
        Assert.Equal(3, chunks[1].PageStart);
        Assert.Equal(3, chunks[1].PageEnd);
    }

    [Fact]
    public async Task Empty_document_yields_no_chunks()
    {
        Assert.Empty(await Run(null));
        Assert.Empty(await Run(null, Para("   \n ", ["H"]), new DocumentBlock(BlockKind.Heading, "H", ["H"])));
    }

    [Theory]
    [InlineData(15, 0.1, "MaxTokens")]
    [InlineData(512, -0.1, "OverlapRatio")]
    [InlineData(512, 0.5, "OverlapRatio")]
    public async Task Invalid_options_throw(int max, double ratio, string name)
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Run(new ChunkingOptions { MaxTokens = max, OverlapRatio = ratio }, Para("x", ["H"])));
        Assert.Equal(name, ex.ParamName);
    }

    [Fact]
    public void Embedding_text_prefixes_heading_path()
    {
        Assert.Equal("A > B\n\nbody", EmbeddingText.For(["A", "B"], "body"));
        Assert.Equal("body", EmbeddingText.For([], "body"));
    }
}
