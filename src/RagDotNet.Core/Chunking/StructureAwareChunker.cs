using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;
using RagDotNet.Documents;

namespace RagDotNet.Chunking;

/// <summary>
/// Splits documents along their structure: never across headings, merging small blocks,
/// and splitting oversized blocks at sentences or lines, then by tokens as a last resort.
/// </summary>
public sealed partial class StructureAwareChunker(Tokenizer tokenizer) : IChunker
{
    private readonly TokenCounter _counter = new(tokenizer);

    /// <inheritdoc />
    public ChunkingStrategy Strategy => ChunkingStrategy.StructureAware;

    /// <inheritdoc />
    public Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxTokens < 16)
            throw new ArgumentOutOfRangeException(nameof(options.MaxTokens), options.MaxTokens, "MaxTokens must be at least 16.");
        if (!(options.OverlapRatio >= 0 && options.OverlapRatio < 0.5))
            throw new ArgumentOutOfRangeException(nameof(options.OverlapRatio), options.OverlapRatio, "OverlapRatio must be in [0, 0.5).");
        if (options.Strategy != ChunkingStrategy.StructureAware)
            throw new ArgumentOutOfRangeException(nameof(options.Strategy), options.Strategy, "Unsupported chunking strategy.");

        var chunks = new List<Chunk>();
        var overlapBudget = (int)Math.Floor(options.MaxTokens * options.OverlapRatio);

        foreach (var section in GroupSections(document.Blocks))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pieces = new List<Piece>();
            foreach (var block in section)
                AddPieces(pieces, block, options.MaxTokens);
            EmitSection(chunks, section[0].HeadingPath, pieces, options.MaxTokens, overlapBudget);
        }

        return Task.FromResult<IReadOnlyList<Chunk>>(chunks);
    }

    private static List<List<DocumentBlock>> GroupSections(IReadOnlyList<DocumentBlock> blocks)
    {
        var sections = new List<List<DocumentBlock>>();
        foreach (var block in blocks)
        {
            if (block.Kind == BlockKind.Heading || string.IsNullOrWhiteSpace(block.Text))
                continue;
            if (sections.Count == 0 || !sections[^1][0].HeadingPath.SequenceEqual(block.HeadingPath))
                sections.Add([]);
            sections[^1].Add(block);
        }

        return sections;
    }

    private void AddPieces(List<Piece> pieces, DocumentBlock block, int maxTokens)
    {
        if (_counter.Count(block.Text) <= maxTokens)
        {
            pieces.Add(new Piece(block.Text, "\n\n", block.PageNumber, block.Kind is BlockKind.Code or BlockKind.Table));
            return;
        }

        var byLines = block.Kind is BlockKind.Code or BlockKind.Table;
        var separator = byLines ? "\n" : " ";
        var units = byLines
            ? block.Text.Split('\n').Select(l => l.TrimEnd('\r'))
            : SentenceSplit().Split(block.Text);

        var first = true;
        foreach (var unit in units)
        {
            if (unit.Length == 0 && !byLines)
                continue;
            if (_counter.Count(unit) <= maxTokens)
            {
                pieces.Add(new Piece(unit, first ? "\n\n" : separator, block.PageNumber, byLines));
            }
            else
            {
                var firstPart = true;
                foreach (var part in _counter.SplitByTokens(unit, maxTokens))
                {
                    pieces.Add(new Piece(part, first ? "\n\n" : firstPart ? separator : string.Empty, block.PageNumber, byLines));
                    first = false;
                    firstPart = false;
                }
            }

            first = false;
        }
    }

    private void EmitSection(List<Chunk> chunks, IReadOnlyList<string> headingPath, List<Piece> pieces, int maxTokens, int overlapBudget)
    {
        var current = new List<Piece>();
        var prefix = string.Empty;
        var currentText = string.Empty;
        string? previousText = null;
        var previousByLines = false;
        var joiner = " ";

        void Flush()
        {
            // A chunk never ends on blank lines; drop them (a chunk of nothing but blank lines is dropped whole).
            var trimmed = false;
            while (current.Count > 0 && string.IsNullOrWhiteSpace(current[^1].Text))
            {
                current.RemoveAt(current.Count - 1);
                trimmed = true;
            }

            if (current.Count == 0)
            {
                currentText = string.Empty;
                return;
            }

            var text = trimmed ? Compose(prefix, current, null) : currentText;
            var pages = current.Where(p => p.Page.HasValue).Select(p => p.Page!.Value).ToList();
            var count = _counter.Count(text);

            // Guard: standalone re-tokenization can differ from the prefix counts used while splitting.
            var texts = count <= maxTokens ? [text] : _counter.SplitByTokens(text, maxTokens).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            foreach (var part in texts)
            {
                chunks.Add(new Chunk(
                    chunks.Count,
                    part,
                    headingPath,
                    pages.Count > 0 ? pages.Min() : null,
                    pages.Count > 0 ? pages.Max() : null,
                    ReferenceEquals(part, text) ? count : _counter.Count(part)));
            }

            if (texts.Count > 0)
                previousText = texts[^1];
            previousByLines = current[^1].ByLines;
            current.Clear();
            currentText = string.Empty;
        }

        string Compose(string pre, List<Piece> items, Piece? extra)
        {
            var sb = new System.Text.StringBuilder(pre);
            var all = extra is null ? items : [.. items, extra];
            for (var i = 0; i < all.Count; i++)
            {
                if (i > 0)
                    sb.Append(all[i].Separator);
                else if (pre.Length > 0)
                    sb.Append(joiner);
                sb.Append(all[i].Text);
            }

            return sb.ToString();
        }

        foreach (var piece in pieces)
        {
            if (current.Count > 0)
            {
                var candidate = Compose(prefix, current, piece);
                if (_counter.Count(candidate) <= maxTokens)
                {
                    current.Add(piece);
                    currentText = candidate;
                    continue;
                }

                Flush();
            }

            // A chunk never starts on a blank line.
            if (string.IsNullOrWhiteSpace(piece.Text))
                continue;

            // Start a new chunk, with overlap from the previous one when the piece still fits.
            joiner = previousByLines ? "\n" : " ";
            prefix = previousText is null ? string.Empty : OverlapSuffix(previousText, overlapBudget, previousByLines);
            var text = Compose(prefix, [], piece);
            if (prefix.Length > 0 && _counter.Count(text) > maxTokens)
            {
                prefix = string.Empty;
                text = piece.Text;
            }

            current.Add(piece);
            currentText = text;
        }

        Flush();
    }

    private string OverlapSuffix(string previous, int budget, bool byLines)
    {
        if (budget <= 0)
            return string.Empty;

        var starts = new List<int> { 0 };
        if (byLines)
        {
            for (var i = 0; i < previous.Length; i++)
                if (previous[i] == '\n' && i + 1 < previous.Length && previous[i + 1] != '\n')
                    starts.Add(i + 1);
        }
        else
        {
            foreach (Match m in SentenceSplit().Matches(previous))
                starts.Add(m.Index + m.Length);
        }

        var best = string.Empty;
        for (var i = starts.Count - 1; i >= 1; i--)
        {
            var suffix = previous[starts[i]..];
            if (suffix.Length == 0)
                continue;
            if (_counter.Count(suffix) > budget)
                break;
            best = suffix;
        }

        return best;
    }

    [GeneratedRegex(@"(?<=[.!?。！？])\s+")]
    private static partial Regex SentenceSplit();

    private sealed record Piece(string Text, string Separator, int? Page, bool ByLines);
}
