using RagDotNet.Evaluation.Datasets;
using static RagDotNet.Evaluation.Tests.TestData;

namespace RagDotNet.Evaluation.Tests;

public class DatasetSetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> DocA = new() { ["doc-a"] = "# A\n\nThe default theme is called Parchment.\n" };
    private static readonly Dictionary<string, string> DocB = new() { ["doc-b"] = "# B\n\nRun the sync command to force a sync now.\n" };
    private const string QA = """[ { "id": "qa", "question": "Theme?", "documentId": "doc-a", "evidence": "default theme is called parchment" } ]""";
    private const string QB = """[ { "id": "qb", "question": "Sync?", "documentId": "doc-b", "evidence": "run the sync command to force a sync" } ]""";

    [Fact]
    public async Task Base_set_hash_is_unchanged_from_stage3()
    {
        var sets = await DatasetLoader.LoadSetsAsync(Path.Combine(AppContext.BaseDirectory, "Dataset"), Ct);

        var b = Assert.Single(sets, s => s.Name == "base").Dataset;
        Assert.Equal("cb1f2ea1af73", b.Hash);
        Assert.Equal(8, b.Documents.Count);
        Assert.Equal(40, b.Questions.Count);
    }

    [Fact]
    public async Task Root_with_corpus_is_a_single_set_named_after_the_directory()
    {
        var dir = SmallDataset();

        var set = Assert.Single(await DatasetLoader.LoadSetsAsync(dir, Ct));

        Assert.Equal(Path.GetFileName(dir), set.Name);
        Assert.Equal(3, set.Dataset.Questions.Count);
    }

    [Fact]
    public async Task Sets_load_in_ordinal_name_order()
    {
        var root = WriteSets(("b", DocB, QB), ("a", DocA, QA));

        var sets = await DatasetLoader.LoadSetsAsync(root, Ct);

        Assert.Equal(["a", "b"], sets.Select(s => s.Name));
    }

    [Fact] // Review Focus 4
    public async Task Duplicate_ids_across_sets_are_rejected_with_both_set_names()
    {
        var root = WriteSets(("a", DocA, QA), ("b", DocA, QA));

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadSetsAsync(root, Ct));

        Assert.Contains("document id 'doc-a' appears in sets a and b", ex.Errors);
        Assert.Contains("question id 'qa' appears in sets a and b", ex.Errors);
    }

    [Fact]
    public async Task Errors_are_prefixed_with_the_set_name()
    {
        var bad = """[ { "id": "q1", "question": "?", "documentId": "doc-b", "evidence": "this evidence is not anywhere" } ]""";
        var root = WriteSets(("a", DocA, QA), ("b", DocB, bad));

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadSetsAsync(root, Ct));

        Assert.Equal(["b: q1: evidence not found in doc-b"], ex.Errors);
    }

    [Fact] // A stale build output can hold the old corpus/ beside the new set directories.
    public async Task Root_with_both_corpus_and_sets_is_rejected()
    {
        var root = WriteSets(("a", DocA, QA));
        Directory.Move(WriteDataset(DocB, QB), Path.Combine(root, "tmp"));
        Directory.Move(Path.Combine(root, "tmp", "corpus"), Path.Combine(root, "corpus"));

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadSetsAsync(root, Ct));

        Assert.Contains(ex.Errors, e => e.Contains("both corpus/ and dataset set directories", StringComparison.Ordinal));
    }

    [Fact] // Deferred minor 8: the report's pooled section is called "all"
    public async Task Set_named_all_is_rejected()
    {
        var root = WriteSets(("a", DocA, QA), ("all", DocB, QB));

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadSetsAsync(root, Ct));

        Assert.Contains(ex.Errors, e => e.Contains("'all'", StringComparison.Ordinal) && e.Contains("reserved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Root_without_sets_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "ragdotnet-eval-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadSetsAsync(root, Ct));

        Assert.Contains(ex.Errors, e => e.Contains("no dataset", StringComparison.Ordinal));
    }
}
