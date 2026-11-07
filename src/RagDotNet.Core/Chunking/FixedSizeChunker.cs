using System.Text;
using Microsoft.ML.Tokenizers;
using RagDotNet.Documents;

namespace RagDotNet.Chunking;

/// <summary>
/// Cuts the document's body text into windows of <see cref="ChunkingOptions.MaxTokens"/> tokens, each repeating the last
/// <c>floor(MaxTokens × OverlapRatio)</c> tokens of the one before. Headings are left out of the text; each chunk's
/// <see cref="Chunk.HeadingPath"/> is that of the block where the window starts.
/// </summary>
public sealed class FixedSizeChunker(Tokenizer tokenizer) : IChunker
{
    private readonly Tokenizer _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));

    /// <inheritdoc />
    public string Name => Chunkers.FixedSize;

    /// <inheritdoc />
    public string Version => "1";

    /// <inheritdoc />
    public Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxTokens < 16)
            throw new ArgumentOutOfRangeException(nameof(options.MaxTokens), options.MaxTokens, "MaxTokens must be at least 16.");
        if (!(options.OverlapRatio >= 0 && options.OverlapRatio < 0.5))
            throw new ArgumentOutOfRangeException(nameof(options.OverlapRatio), options.OverlapRatio, "OverlapRatio must be in [0, 0.5).");

        // Flatten the body blocks, remembering where each one lies in the text.
        var text = new StringBuilder();
        var spans = new List<(int Start, int End, DocumentBlock Block)>();
        foreach (var block in document.Blocks)
        {
            if (block.Kind == BlockKind.Heading || string.IsNullOrWhiteSpace(block.Text)) continue;
            if (text.Length > 0) text.Append("\n\n");
            var start = text.Length;
            text.Append(block.Text);
            spans.Add((start, text.Length, block));
        }

        var chunks = new List<Chunk>();
        if (spans.Count == 0) return Task.FromResult<IReadOnlyList<Chunk>>(chunks);

        var flat = text.ToString();
        var tokens = _tokenizer.EncodeToTokens(flat, out _, considerPreTokenization: true, considerNormalization: false);
        var overlap = (int)Math.Floor(options.MaxTokens * options.OverlapRatio);
        int Start(int t) => tokens[t].Offset.Start.GetOffset(flat.Length);
        int End(int t) => tokens[t].Offset.End.GetOffset(flat.Length);

        // A character the tokenizer splits into several byte-level tokens gives each of them the whole character's
        // offsets, so a window edge must never fall between them: the character would be sliced into both windows.
        bool SplitsCharacter(int boundary) => boundary > 0 && boundary < tokens.Count && Start(boundary) < End(boundary - 1);

        var first = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var last = Math.Min(first + options.MaxTokens, tokens.Count) - 1;
            while (last > first && SplitsCharacter(last + 1)) last--;
            var charStart = Start(first);
            var charEnd = Math.Max(charStart, End(last));
            var window = flat[charStart..charEnd].Trim();
            if (window.Length > 0)
            {
                var touched = spans.Where(s => s.End > charStart && s.Start < charEnd).ToList();
                var startBlock = touched.Count > 0 ? touched[0].Block : spans.Last(s => s.Start <= charStart).Block;
                var pages = touched.Select(s => s.Block.PageNumber).OfType<int>().ToList();
                chunks.Add(new Chunk(chunks.Count, window, startBlock.HeadingPath,
                    pages.Count > 0 ? pages.Min() : null, pages.Count > 0 ? pages.Max() : null, 0));
            }

            if (last == tokens.Count - 1) break;

            // The next window repeats the last `overlap` tokens, moved forward to a character boundary.
            var next = Math.Max(first + 1, last + 1 - overlap);
            while (SplitsCharacter(next)) next++;
            first = next;
        }

        return Task.FromResult<IReadOnlyList<Chunk>>(chunks);
    }
}
