namespace RagDotNet.Chunking;

/// <summary>Enforces the library's guarantees on whatever a chunker returns.</summary>
internal static class ChunkOutputGuard
{
    public static IReadOnlyList<Chunk> Normalize(IChunker chunker, IReadOnlyList<Chunk>? output, ChunkingOptions options, TokenCounter tokens)
    {
        if (output is null || output.Any(c => c is null))
            throw new InvalidOperationException($"Chunker '{chunker.Name}' returned a null chunk list or a null chunk.");

        var result = new List<Chunk>(output.Count);
        foreach (var original in output)
        {
            if (string.IsNullOrWhiteSpace(original.Text)) continue;
            var chunk = original.HeadingPath is null ? original with { HeadingPath = [] } : original;
            if (tokens.Count(chunk.Text) <= options.MaxTokens)
            {
                result.Add(chunk);
                continue;
            }

            foreach (var piece in tokens.SplitByTokens(chunk.Text, options.MaxTokens))
                if (!string.IsNullOrWhiteSpace(piece))
                    result.Add(chunk with { Text = piece });
        }

        for (var i = 0; i < result.Count; i++)
            result[i] = result[i] with { Index = i, TokenCount = tokens.Count(result[i].Text) };
        return result;
    }
}
