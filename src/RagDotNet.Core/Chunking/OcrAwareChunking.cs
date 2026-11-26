using RagDotNet.Documents;

namespace RagDotNet.Chunking;

/// <summary>
/// Chunks a document whose OCR text has its own chunk size (<see cref="Ocr.OcrOptions.ChunkMaxTokens"/>). Each run of
/// consecutive OCR blocks, and each run of other blocks, is chunked on its own with the same chunker, so no chunk mixes the
/// two; the chunks keep document order and are numbered from zero.
/// </summary>
internal static class OcrAwareChunking
{
    public static async Task<IReadOnlyList<Chunk>> ChunkAsync(
        IChunker chunker, ParsedDocument document, ChunkingOptions chunking, int? ocrMaxTokens, TokenCounter tokens, CancellationToken ct)
    {
        if (ocrMaxTokens is not { } ocrMax || !document.Blocks.Any(b => b.IsOcrText))
            return ChunkOutputGuard.Normalize(chunker, await chunker.ChunkAsync(document, chunking, ct).ConfigureAwait(false), chunking, tokens);

        var ocrChunking = chunking with { MaxTokens = ocrMax };
        var result = new List<Chunk>();
        foreach (var (isOcr, blocks) in Runs(document.Blocks))
        {
            var options = isOcr ? ocrChunking : chunking;
            var part = document with { Blocks = blocks };
            result.AddRange(ChunkOutputGuard.Normalize(chunker, await chunker.ChunkAsync(part, options, ct).ConfigureAwait(false), options, tokens));
        }

        for (var i = 0; i < result.Count; i++)
            result[i] = result[i] with { Index = i };
        return result;
    }

    /// <summary>
    /// Splits the blocks where OCR text starts or stops. A run that starts inside a section begins with the document's own
    /// heading blocks above it, so a chunker that reads headings from heading blocks (rather than from each block's
    /// <see cref="DocumentBlock.HeadingPath"/>) still sees them; heading blocks produce no chunk text of their own.
    /// </summary>
    private static IEnumerable<(bool IsOcr, List<DocumentBlock> Blocks)> Runs(IReadOnlyList<DocumentBlock> blocks)
    {
        var run = new List<DocumentBlock>();
        var isOcr = false;
        var headings = new List<DocumentBlock>(); // the heading blocks on the path at the current position
        foreach (var block in blocks)
        {
            if (run.Count > 0 && block.IsOcrText != isOcr)
            {
                yield return (isOcr, run);
                run = [.. headings.Where(h => IsAbove(h, block))];
            }

            isOcr = block.IsOcrText;
            run.Add(block);
            if (block.Kind == BlockKind.Heading && block.HeadingPath.Count > 0)
            {
                headings.RemoveAll(h => h.HeadingPath.Count >= block.HeadingPath.Count);
                headings.Add(block);
            }
        }

        if (run.Count > 0) yield return (isOcr, run);
    }

    /// <summary>Whether <paramref name="heading"/> is on the path of <paramref name="block"/>, above it.</summary>
    private static bool IsAbove(DocumentBlock heading, DocumentBlock block)
    {
        var limit = block.Kind == BlockKind.Heading ? block.HeadingPath.Count - 1 : block.HeadingPath.Count;
        return heading.HeadingPath.Count <= limit
            && heading.HeadingPath.SequenceEqual(block.HeadingPath.Take(heading.HeadingPath.Count), StringComparer.Ordinal);
    }
}
