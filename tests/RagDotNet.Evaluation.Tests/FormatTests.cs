using RagDotNet.Evaluation.Datasets;
using static RagDotNet.Evaluation.Tests.TestData;

namespace RagDotNet.Evaluation.Tests;

public class FormatTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BaseSet => Path.Combine(AppContext.BaseDirectory, "Dataset", "base");

    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("html")]
    public void Every_base_document_exists_in_every_format(string format)
    {
        var md = Directory.GetFiles(Path.Combine(BaseSet, "corpus"), "*.md").Select(Path.GetFileNameWithoutExtension).Order();
        var variant = Directory.GetFiles(Path.Combine(BaseSet, "formats", format), "*." + format).Select(Path.GetFileNameWithoutExtension).Order();

        Assert.Equal(md, variant);
    }

    [Fact]
    public async Task Format_variants_load_with_the_set_questions_and_their_own_hash()
    {
        var sets = await DatasetLoader.LoadSetsAsync(BaseSet, ["md", "pdf"], Ct);

        Assert.Equal(["md", "pdf"], sets.Select(s => s.Format));
        Assert.All(sets, s => Assert.Equal("base", s.Name));
        Assert.Equal(sets[0].Dataset.Questions, sets[1].Dataset.Questions);
        Assert.Equal("cb1f2ea1af73", sets[0].Dataset.Hash);
        Assert.NotEqual(sets[0].Dataset.Hash, sets[1].Dataset.Hash);
        Assert.Equal(["base", "base · pdf"], sets.Select(s => s.Label));
    }

    [Fact]
    public async Task Html_variant_has_no_unscorable_questions()
    {
        var html = Assert.Single(await DatasetLoader.LoadSetsAsync(BaseSet, ["html"], Ct));

        Assert.Equal(0, html.Unscorable);
        Assert.Equal(8, html.Dataset.Documents.Count);
    }

    [Fact]
    public async Task Unscorable_questions_are_counted_not_rejected()
    {
        var dir = WriteDataset(
            new Dictionary<string, string> { ["a"] = "# A\n\nThe default theme is called Parchment.\n\nPress Ctrl+K to open the command palette.\n" },
            """
            [
              { "id": "q1", "question": "Theme?", "documentId": "a", "evidence": "default theme is called parchment" },
              { "id": "q2", "question": "Palette?", "documentId": "a", "evidence": "press ctrl+k to open the command palette" }
            ]
            """);
        Directory.CreateDirectory(Path.Combine(dir, "formats", "html"));
        await File.WriteAllTextAsync(Path.Combine(dir, "formats", "html", "a.html"),
            "<html><body><main><h1>A</h1><p>The default theme is called Parchment.</p><p>Press Ctrl+K to open the palette.</p></main></body></html>", Ct);

        var html = Assert.Single(await DatasetLoader.LoadSetsAsync(dir, ["html"], Ct));

        Assert.Equal(1, html.Unscorable);
        Assert.Equal(2, html.Dataset.Questions.Count);
    }

    [Fact]
    public async Task Missing_document_in_a_variant_is_an_error()
    {
        var dir = WriteDataset(
            new Dictionary<string, string> { ["a"] = "# A\n\nThe default theme is called Parchment.\n", ["b"] = "# B\n\nOther text that is long enough.\n" },
            """[ { "id": "q1", "question": "Theme?", "documentId": "a", "evidence": "default theme is called parchment" } ]""");
        Directory.CreateDirectory(Path.Combine(dir, "formats", "html"));
        await File.WriteAllTextAsync(Path.Combine(dir, "formats", "html", "a.html"), "<p>The default theme is called Parchment.</p>", Ct);

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadSetsAsync(dir, ["html"], Ct));

        Assert.Contains(ex.Errors, e => e.Contains("html", StringComparison.Ordinal) && e.Contains("'b'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Report_has_a_section_per_format_and_all_pools_md_only()
    {
        var (code, report, err) = await CliTests.Run("--dataset", BaseSet, "--formats", "md,html", "--chunkers", "structure-aware", "--max-tokens", "512", "--overlap", "0.1");

        Assert.True(code == 0, err);
        var headings = report.Split('\n').Where(l => l.StartsWith("## ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["## base", "## base · html"], headings);
        Assert.Contains("- Set `base` · html: 8 documents, 40 questions, hash ", report, StringComparison.Ordinal);
        Assert.Contains("## base · html\n\n- Unscorable questions: 0\n", report, StringComparison.Ordinal);
    }

    [Fact] // Final review minor: an unscorable question could still score a hit (e.g. evidence found twice)
    public async Task Unscorable_questions_always_score_as_a_miss()
    {
        var dir = WriteDataset(
            new Dictionary<string, string> { ["a"] = "# A\n\nThe default theme is called Parchment.\n" },
            """[ { "id": "q1", "question": "What is the default theme called?", "documentId": "a", "evidence": "default theme is called parchment" } ]""");
        Directory.CreateDirectory(Path.Combine(dir, "formats", "html"));
        await File.WriteAllTextAsync(Path.Combine(dir, "formats", "html", "a.html"),
            "<main><p>The default theme is called Parchment.</p><p>Again: the default theme is called Parchment.</p></main>", Ct);
        var sets = await DatasetLoader.LoadSetsAsync(dir, ["html"], Ct);
        using var embedder = new Providers.FakeEmbeddingGenerator(256);

        var results = await new Running.EvaluationRunner(embedder, 256, chat: null)
            .RunAsync(sets, Running.EvaluationConfig.Matrix(["structure-aware"], [512], [0], ["vector"]), Ct);

        Assert.Equal(1, sets[0].Unscorable);
        Assert.Equal([null], Assert.Single(results).Ranks);
    }

    [Fact] // Final review minor: asking for a format no set has gave an empty report and exit 0
    public async Task A_format_no_set_has_exits_1()
    {
        var (code, report, err) = await CliTests.Run("--dataset", SmallDataset(), "--formats", "pdf");

        Assert.Equal(1, code);
        Assert.Equal("", report);
        Assert.Contains("pdf", err, StringComparison.Ordinal);
    }

    [Fact] // Final review minor: an unreadable file in a rendered format escaped as an unhandled exception
    public async Task An_unreadable_variant_file_is_a_dataset_error()
    {
        var dir = WriteDataset(
            new Dictionary<string, string> { ["a"] = "# A\n\nThe default theme is called Parchment.\n" },
            """[ { "id": "q1", "question": "Theme?", "documentId": "a", "evidence": "default theme is called parchment" } ]""");
        Directory.CreateDirectory(Path.Combine(dir, "formats", "pdf"));
        await File.WriteAllBytesAsync(Path.Combine(dir, "formats", "pdf", "a.pdf"), [1, 2, 3, 4, 5], Ct);

        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadSetsAsync(dir, ["pdf"], Ct));

        Assert.Contains(ex.Errors, e => e.Contains("a.pdf", StringComparison.Ordinal) && e.Contains("· pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_format_exits_1()
    {
        var (code, _, err) = await CliTests.Run("--formats", "md,rtf");

        Assert.Equal(1, code);
        Assert.Contains("--formats", err, StringComparison.Ordinal);
    }
}
