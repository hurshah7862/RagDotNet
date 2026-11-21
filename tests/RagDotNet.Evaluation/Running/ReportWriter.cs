using System.Globalization;
using System.Text;
using RagDotNet.Evaluation.Datasets;
using RagDotNet.Evaluation.Scoring;

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

    /// <summary>The name of the section that pools every set's questions.</summary>
    public const string AllSection = "all";

    /// <summary>
    /// Renders the report: metadata, then a section per set with its table and, when there are two or more sets, an
    /// <see cref="AllSection"/> section whose chunk counts are summed and whose scores pool every question.
    /// </summary>
    public static string Write(ReportMetadata meta, IReadOnlyList<DatasetSet> sets, IReadOnlyList<ConfigResult> results)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(results);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        // Reports of vector-only runs keep their pre-hybrid layout byte for byte.
        var showRetrieval = results.Any(r => r.Config.Retrieval != EvaluationConfig.RetrievalVector);
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("# RagDotNet retrieval evaluation");
        Line();
        Line($"- Embedder: {meta.Embedder} ({meta.EmbedModel})");
        Line($"- Chat model: {meta.ChatModel}");
        foreach (var set in sets)
        {
            var format = set.Format == DatasetSet.Markdown ? "" : " · " + set.Format;
            Line(string.Create(inv, $"- Set `{set.Name}`{format}: {set.Dataset.Documents.Count} documents, {set.Dataset.Questions.Count} questions, hash {set.Dataset.Hash}"));
        }

        if (meta.Date is { } date)
        {
            Line($"- Date: {date.ToString("yyyy-MM-dd", inv)}");
        }

        foreach (var set in sets)
        {
            var note = set.Format == DatasetSet.Markdown ? null : string.Create(inv, $"- Unscorable questions: {set.Unscorable}");
            if (set.Format == DatasetSet.Scanned)
            {
                // Under strict matching, a question is unscorable exactly when OCR damaged its evidence.
                var total = set.Dataset.Questions.Count;
                note += string.Create(inv, $"\n- Evidence intact after OCR: {total - set.Unscorable} of {total}");
            }

            var rows = results.Where(r => r.Set == set.Label).ToList();
            Table(set.Label, rows, note);
            var exact = set.Dataset.Questions
                .Select((q, i) => (q, i))
                .Where(x => x.q.Tags?.Contains(Question.ExactTerm) == true)
                .Select(x => x.i)
                .ToList();
            if (showRetrieval && exact.Count > 0)
            {
                // Questions whose answer hinges on an exact term, where keyword search should help most.
                Table(string.Create(inv, $"{set.Label} · {Question.ExactTerm} ({exact.Count} questions)"),
                    [.. rows.Select(r => r with { Scores = Metrics.Summarize([.. exact.Select(i => r.Ranks[i])]) })],
                    heading: "###");
            }
        }

        // The pooled section covers the sets' own Markdown only, so it stays comparable across runs with other formats.
        var markdown = sets.Where(s => s.Format == DatasetSet.Markdown).Select(s => s.Label).ToHashSet(StringComparer.Ordinal);
        if (markdown.Count > 1)
        {
            Table(AllSection, [.. results
                .Where(r => markdown.Contains(r.Set))
                .GroupBy(r => r.Config)
                .Select(g =>
                {
                    var ranks = g.SelectMany(r => r.Ranks).ToList();
                    return new ConfigResult(AllSection, g.Key, g.Sum(r => r.ChunkCount), Metrics.Summarize(ranks), ranks);
                })]);
        }

        void Table(string title, IReadOnlyList<ConfigResult> rows, string? note = null, string heading = "##")
        {
            Line();
            Line($"{heading} {title}");
            Line();
            if (note is not null)
            {
                Line(note);
                Line();
            }

            Line(showRetrieval
                ? "| Chunker | MaxTokens | Overlap | Mode | Retrieval | Chunks | R@1 | R@3 | R@5 | MRR |"
                : "| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |");
            Line(showRetrieval ? "|---|---:|---:|---|---|---:|---:|---:|---:|---:|" : "|---|---:|---:|---|---:|---:|---:|---:|---:|");
            foreach (var r in rows)
            {
                var c = r.Config;
                var s = r.Scores;
                var overlap = c.UsesOverlap ? c.Overlap.ToString("0.###", inv) : "n/a";
                Line(string.Create(inv,
                    $"| {c.Chunker} | {c.MaxTokens} | {overlap} | {c.Mode} |{(showRetrieval ? $" {c.Retrieval} |" : "")} {r.ChunkCount} | {s.RecallAt1:0.000} | {s.RecallAt3:0.000} | {s.RecallAt5:0.000} | {s.Mrr:0.000} |"));
            }
        }

        if (meta.Fake)
        {
            Line();
            Line(FakeNote);
        }

        return sb.ToString();
    }
}
