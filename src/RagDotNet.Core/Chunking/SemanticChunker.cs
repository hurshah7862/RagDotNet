using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using RagDotNet.Documents;

namespace RagDotNet.Chunking;

/// <summary>
/// Splits body text into sentences (a table or code block counts as one), embeds each, and starts a new chunk where the
/// cosine distance between neighbours exceeds the <see cref="ChunkingOptions.BreakpointPercentile"/>-th percentile of the
/// document's distances. A heading change always starts a new chunk, and no chunk grows past
/// <see cref="ChunkingOptions.MaxTokens"/>. <see cref="ChunkingOptions.OverlapRatio"/> is ignored.
/// Makes one embedding per sentence during ingest.
/// </summary>
public sealed class SemanticChunker : IChunker
{
    private readonly TokenCounter _counter;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly int _batchSize;

    /// <summary>Creates the chunker.</summary>
    /// <param name="tokenizer">Tokenizer used to keep chunks within <see cref="ChunkingOptions.MaxTokens"/>.</param>
    /// <param name="embedder">Embeds the sentences.</param>
    /// <param name="batchSize">Sentences per embedding call; at least 1.</param>
    public SemanticChunker(Tokenizer tokenizer, IEmbeddingGenerator<string, Embedding<float>> embedder, int batchSize = 32)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _counter = new TokenCounter(tokenizer);
        _embedder = embedder;
        _batchSize = batchSize;
    }

    /// <inheritdoc />
    public string Name => Chunkers.Semantic;

    /// <inheritdoc />
    public string Version => "1";

    /// <inheritdoc />
    public async Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxTokens < 16)
            throw new ArgumentOutOfRangeException(nameof(options.MaxTokens), options.MaxTokens, "MaxTokens must be at least 16.");
        if (!(options.BreakpointPercentile > 0 && options.BreakpointPercentile <= 100))
            throw new ArgumentOutOfRangeException(nameof(options.BreakpointPercentile), options.BreakpointPercentile, "BreakpointPercentile must be in (0, 100].");

        var units = new List<Unit>();
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            var block = document.Blocks[i];
            if (block.Kind == BlockKind.Heading || string.IsNullOrWhiteSpace(block.Text)) continue;
            if (block.Kind is BlockKind.Code or BlockKind.Table) units.Add(new Unit(block.Text.Trim(), block, i));
            else units.AddRange(SentenceSplitter.Split(block.Text.Trim()).Select(s => new Unit(s.Trim(), block, i)));
        }

        if (units.Count == 0) return [];

        var vectors = new List<ReadOnlyMemory<float>>(units.Count);
        foreach (var batch in units.Chunk(_batchSize))
        {
            var embeddings = await _embedder.GenerateAsync(batch.Select(u => u.Text), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (embeddings.Count != batch.Length)
                throw new InvalidOperationException($"The embedding generator returned {embeddings.Count} vectors for {batch.Length} sentences.");
            vectors.AddRange(embeddings.Select(e => e.Vector));
        }

        // distances[i] is between unit i and i + 1; null where a section boundary forces a break anyway.
        var distances = new double?[units.Count - 1];
        for (var i = 0; i < distances.Length; i++)
            if (SameSection(units[i], units[i + 1])) distances[i] = 1 - Cosine(vectors[i].Span, vectors[i + 1].Span);
        var threshold = Percentile([.. distances.OfType<double>()], options.BreakpointPercentile);

        var chunks = new List<Chunk>();
        var current = new List<Unit>();
        for (var i = 0; i < units.Count; i++)
        {
            var breakBefore = i > 0 && (distances[i - 1] is not { } d || d > threshold);
            if (current.Count > 0 && (breakBefore || _counter.Count(Join([.. current, units[i]])) > options.MaxTokens))
            {
                chunks.Add(ToChunk(chunks.Count, current));
                current = [];
            }

            current.Add(units[i]);
        }

        chunks.Add(ToChunk(chunks.Count, current));
        return chunks;
    }

    private static bool SameSection(Unit a, Unit b) => a.Block.HeadingPath.SequenceEqual(b.Block.HeadingPath, StringComparer.Ordinal);

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>The p-th percentile with linear interpolation between the two nearest ranks; +∞ when there are no values.</summary>
    private static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return double.PositiveInfinity;
        Array.Sort(values);
        var position = p / 100 * (values.Length - 1);
        var lo = (int)Math.Floor(position);
        var hi = (int)Math.Ceiling(position);
        return values[lo] + ((values[hi] - values[lo]) * (position - lo));
    }

    private static string Join(IReadOnlyList<Unit> units)
    {
        var sb = new StringBuilder(units[0].Text);
        for (var i = 1; i < units.Count; i++)
            sb.Append(units[i].BlockIndex == units[i - 1].BlockIndex ? " " : "\n\n").Append(units[i].Text);
        return sb.ToString();
    }

    private static Chunk ToChunk(int index, List<Unit> units)
    {
        var pages = units.Select(u => u.Block.PageNumber).OfType<int>().ToList();
        return new Chunk(index, Join(units), units[0].Block.HeadingPath, pages.Count > 0 ? pages.Min() : null, pages.Count > 0 ? pages.Max() : null, 0);
    }

    private sealed record Unit(string Text, DocumentBlock Block, int BlockIndex);
}
