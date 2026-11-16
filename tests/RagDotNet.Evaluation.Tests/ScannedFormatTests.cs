using RagDotNet.Evaluation.Datasets;
using RagDotNet.Evaluation.Running;
using UglyToad.PdfPig;
using static RagDotNet.Evaluation.Tests.TestData;

namespace RagDotNet.Evaluation.Tests;

/// <summary>The scanned check: the base set as image-only PDFs, read with Tesseract.</summary>
public class ScannedFormatTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BaseSet => Path.Combine(AppContext.BaseDirectory, "Dataset", "base");

    [Fact]
    public void Scanned_pdfs_have_no_text_layer()
    {
        var md = Directory.GetFiles(Path.Combine(BaseSet, "corpus"), "*.md").Select(Path.GetFileNameWithoutExtension).Order();
        var files = Directory.GetFiles(Path.Combine(BaseSet, "formats", "scanned"), "*.pdf").Order().ToList();

        Assert.Equal(md, files.Select(Path.GetFileNameWithoutExtension));
        foreach (var file in files)
        {
            using var pdf = PdfDocument.Open(file);
            Assert.All(pdf.GetPages(), p => Assert.Empty(p.Letters));
            Assert.All(pdf.GetPages(), p => Assert.NotEmpty(p.GetImages()));
        }
    }

    [Fact]
    public async Task Loading_scanned_without_ocr_is_a_dataset_error()
    {
        var ex = await Assert.ThrowsAsync<DatasetValidationException>(() => DatasetLoader.LoadSetsAsync(BaseSet, ["scanned"], ocr: null, Ct));

        Assert.Contains(ex.Errors, e => e.Contains("base · scanned", StringComparison.Ordinal) && e.Contains("OCR", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanned_variant_report_states_evidence_intact_after_ocr()
    {
        var questions = Enumerable.Range(1, 40).Select(i => Q("a", "x", "q" + i)).ToList();
        var set = new DatasetSet("base", new Dataset([], questions, "abc"), "scanned", ["q1", "q2", "q3"]);
        var config = EvaluationConfig.Matrix(["structure-aware"], [512], [0.1], ["vector"]).Single();
        var result = new ConfigResult(set.Label, config, 10, Scoring.Metrics.Summarize([.. Enumerable.Repeat<int?>(null, 40)]), [.. Enumerable.Repeat<int?>(null, 40)]);

        var report = ReportWriter.Write(new ReportMetadata("fake", "fake", "none", null, true), [set], [result]);

        Assert.Contains("## base · scanned\n\n- Unscorable questions: 3\n- Evidence intact after OCR: 37 of 40\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_run_over_scanned_scores_and_reports()
    {
        var tessdata = TessData();
        var (code, report, err) = await CliTests.Run(
            "--dataset", BaseSet, "--formats", "scanned", "--tessdata", tessdata,
            "--chunkers", "structure-aware", "--max-tokens", "512", "--overlap", "0.1");

        Assert.True(code == 0, err);
        Assert.Contains("## base · scanned", report, StringComparison.Ordinal);
        Assert.Matches(@"- Evidence intact after OCR: \d+ of 40", report);
    }

    [Fact]
    public async Task Scanned_with_missing_language_data_exits_1_naming_the_file()
    {
        var (code, report, err) = await CliTests.Run("--dataset", BaseSet, "--formats", "scanned", "--tessdata", "/nowhere/tessdata");

        Assert.Equal(1, code);
        Assert.Equal("", report);
        Assert.Contains("eng.traineddata", err, StringComparison.Ordinal);
    }

    /// <summary>Tesseract's data folder; skipped locally and failed on CI when it is missing.</summary>
    private static string TessData()
    {
        var path = Environment.GetEnvironmentVariable("RAGDOTNET_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata";
        if (File.Exists(Path.Combine(path, "eng.traineddata"))) return path;
        var message = $"Tesseract language data (eng.traineddata) not found in '{path}'; set RAGDOTNET_TESSDATA.";
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)) Assert.Fail(message);
        Assert.Skip(message);
        return path;
    }
}
