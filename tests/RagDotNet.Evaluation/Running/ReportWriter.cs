using System.Globalization;
using System.Text;
using RagDotNet.Evaluation.Datasets;

namespace RagDotNet.Evaluation.Running;

/// <summary>What the report says about how it was produced.</summary>
/// <param name="Embedder">Embedder kind: <c>fake</c> or <c>ollama</c>.</param>
/// <param name="EmbedModel">Embedding model id.</param>
/// <param name="ChatModel">Chat model, or <c>none</c>.</param>
/// <param name="Date">The run date; real mode only.</param>
/// <param name="Fake">Whether the scores come from fake embeddings.</param>
public sealed record ReportMetadata(string Embedder, string EmbedModel, string ChatModel, DateOnly? Date, bool Fake);

/// <summary>Writes the Markdown report. Output uses <c>\n</c> line endings and invariant culture, so it is byte-stable.</summary>
public static class ReportWriter
{
    /// <summary>The fake-mode closing note.</summary>
    public const string FakeNote = "Scores from fake embeddings only check that the harness works; they are not meaningful.";

    /// <summary>Renders the report.</summary>
    public static string Write(ReportMetadata meta, Dataset dataset, IReadOnlyList<ConfigResult> results)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(results);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("# RagDotNet retrieval evaluation");
        Line();
        Line($"- Embedder: {meta.Embedder} ({meta.EmbedModel})");
        Line($"- Chat model: {meta.ChatModel}");
        Line(string.Create(inv, $"- Corpus documents: {dataset.Documents.Count}"));
        Line(string.Create(inv, $"- Questions: {dataset.Questions.Count}"));
        Line($"- Dataset hash: {dataset.Hash}");
        if (meta.Date is { } date)
        {
            Line($"- Date: {date.ToString("yyyy-MM-dd", inv)}");
        }

        Line();
        Line("| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |");
        Line("|---|---:|---:|---|---:|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            var c = r.Config;
            var s = r.Scores;
            Line(string.Create(inv,
                $"| {c.Chunker} | {c.MaxTokens} | {c.Overlap:0.###} | {c.Mode} | {r.ChunkCount} | {s.RecallAt1:0.000} | {s.RecallAt3:0.000} | {s.RecallAt5:0.000} | {s.Mrr:0.000} |"));
        }

        if (meta.Fake)
        {
            Line();
            Line(FakeNote);
        }

        return sb.ToString();
    }
}
