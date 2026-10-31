using RagDotNet.Chunking;
using RagDotNet.Retrieval;

namespace RagDotNet.Generation;

internal static class ContextBudget
{
    public static IReadOnlyList<RetrievedChunk> Select(IReadOnlyList<RetrievedChunk> ranked, int maxTokens, TokenCounter counter)
    {
        var selected = new List<RetrievedChunk>();
        var total = 0;
        foreach (var chunk in ranked)
        {
            var tokens = counter.Count(chunk.Text);
            if (selected.Count > 0 && total + tokens > maxTokens) break;

            // The first chunk is always included, so a retrieved result never yields an empty context.
            selected.Add(chunk);
            total += tokens;
        }

        return selected;
    }
}
