using RagDotNet.Evaluation.Datasets;
using static RagDotNet.Evaluation.Tests.TestData;

namespace RagDotNet.Evaluation.Tests;

public class DatasetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // Review Focus 1
    public async Task Duplicate_or_missing_evidence_is_rejected_with_question_ids()
    {
        var dir = WriteDataset(
            new Dictionary<string, string>
            {
                ["a"] = "# A\n\nalpha beta gamma delta one.\n\nAnd again alpha beta gamma delta two.\n",
                ["b"] = "# B\n\nSomething else entirely lives in this document.\n",
            },
            """
            [
              { "id": "q1", "question": "?", "documentId": "a", "evidence": "alpha beta gamma delta" },
              { "id": "q2", "question": "?", "documentId": "a", "evidence": "this sentence is nowhere at all" },
              { "id": "q3", "question": "?", "documentId": "zzz", "evidence": "something else entirely lives" },
              { "id": "q4", "question": "?", "documentId": "b", "evidence": "something" },
              { "id": "q1", "question": "?", "documentId": "b", "evidence": "something else entirely lives" }
            ]
            """);

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadAsync(dir, Ct));

        Assert.Contains("q1: evidence found 2 times", ex.Errors);
        Assert.Contains("q2: evidence not found in a", ex.Errors);
        Assert.Contains(ex.Errors, e => e.StartsWith("q3:", StringComparison.Ordinal) && e.Contains("zzz", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("q4:", StringComparison.Ordinal) && e.Contains("20", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("duplicate", StringComparison.OrdinalIgnoreCase) && e.Contains("q1", StringComparison.Ordinal));
        Assert.Contains("q1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evidence_in_another_document_than_labelled_is_rejected()
    {
        var dir = WriteDataset(
            new Dictionary<string, string> { ["a"] = "# A\n\nThe quick brown fox jumps high.\n", ["b"] = "# B\n\nNothing here to see.\n" },
            """[ { "id": "q1", "question": "?", "documentId": "b", "evidence": "the quick brown fox jumps" } ]""");

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadAsync(dir, Ct));

        Assert.Contains("q1: evidence not found in b", ex.Errors);
    }

    [Fact] // Review Focus 1: evidence no chunk can contain would always score a miss
    public async Task Evidence_spanning_two_blocks_is_rejected()
    {
        var dir = WriteDataset(
            new Dictionary<string, string> { ["a"] = "# A\n\nThe first paragraph ends here.\n\nThe second paragraph starts here.\n" },
            """[ { "id": "q1", "question": "?", "documentId": "a", "evidence": "ends here. the second paragraph" } ]""");

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadAsync(dir, Ct));

        Assert.Contains(ex.Errors, e => e.StartsWith("q1:", StringComparison.Ordinal) && e.Contains("block", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Valid_dataset_loads_with_stable_hash()
    {
        var docs = new Dictionary<string, string>
        {
            ["b-doc"] = "# B\n\nRun `quill sync --now` to force a sync.\n",
            ["a-doc"] = "# A\n\nThe **default theme** is called Parchment.\n",
        };
        const string questions = """
            [ { "id": "q1", "question": "What is the default theme?", "documentId": "a-doc", "evidence": "default theme is called parchment" } ]
            """;

        var first = await DatasetLoader.LoadAsync(WriteDataset(docs, questions), Ct);
        var second = await DatasetLoader.LoadAsync(WriteDataset(docs, questions), Ct);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Matches("^[0-9a-f]{12}$", first.Hash);
        Assert.Equal(["a-doc", "b-doc"], first.Documents.Select(d => d.DocumentId));
        Assert.Equal("# a the default theme is called parchment.", first.Documents[0].NormalizedText);
        Assert.Equal(new Question("q1", "What is the default theme?", "a-doc", "default theme is called parchment"), first.Questions.Single());
    }

    [Fact] // Review Focus 5
    public async Task Shipped_dataset_is_valid_and_balanced()
    {
        var d = await DatasetLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Dataset"), Ct);

        Assert.Equal(8, d.Documents.Count);
        Assert.InRange(d.Questions.Count, 40, 40);
        Assert.All(d.Documents, doc => Assert.True(d.Questions.Count(q => q.DocumentId == doc.DocumentId) >= 3, doc.DocumentId));
        Assert.All(d.Documents, doc =>
        {
            var words = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(doc.FilePath), @"\w+").Count;
            Assert.True(words is >= 600 and <= 1500, $"{doc.DocumentId}: {words} words");
        });
    }

    [Fact]
    public async Task Shipped_questions_cover_tables_and_code_blocks()
    {
        var d = await DatasetLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Dataset"), Ct);
        var reader = new RagDotNet.Readers.MarkdownReader();
        var kinds = new List<RagDotNet.Documents.BlockKind>();
        foreach (var q in d.Questions)
        {
            var doc = d.Documents.Single(x => x.DocumentId == q.DocumentId);
            await using var stream = File.OpenRead(doc.FilePath);
            var parsed = await reader.ReadAsync(stream, new(Path.GetFileName(doc.FilePath), null), Ct);
            var evidence = Scoring.TextNormalizer.Normalize(q.Evidence);
            var block = parsed.Blocks.FirstOrDefault(b => Scoring.TextNormalizer.Normalize(b.Text).Contains(evidence, StringComparison.Ordinal));
            Assert.True(block is not null, $"{q.Id}: evidence spans blocks");
            kinds.Add(block.Kind);
        }

        Assert.True(kinds.Count(k => k == RagDotNet.Documents.BlockKind.Table) >= 4, "at least 4 table questions");
        Assert.True(kinds.Count(k => k == RagDotNet.Documents.BlockKind.Code) >= 3, "at least 3 code questions");
    }

    [Fact]
    public async Task Shipped_dataset_runs_end_to_end_in_fake_mode()
    {
        var (code, report, err) = await CliTests.Run("--max-tokens", "256", "--overlap", "0");

        Assert.True(code == 0, err);
        var row = Assert.Single(report.Split('\n'), l => l.StartsWith("| structure-aware ", StringComparison.Ordinal));
        var r5 = double.Parse(row.Split('|', StringSplitOptions.TrimEntries)[8], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(r5 > 0, row);
    }

    [Fact]
    public async Task Hash_changes_when_a_question_changes()
    {
        var docs = new Dictionary<string, string> { ["a"] = "# A\n\nThe default theme is called Parchment.\n" };
        var one = await DatasetLoader.LoadAsync(WriteDataset(docs, """[ { "id": "q1", "question": "x?", "documentId": "a", "evidence": "default theme is called parchment" } ]"""), Ct);
        var two = await DatasetLoader.LoadAsync(WriteDataset(docs, """[ { "id": "q1", "question": "y?", "documentId": "a", "evidence": "default theme is called parchment" } ]"""), Ct);
        Assert.NotEqual(one.Hash, two.Hash);
    }
}
