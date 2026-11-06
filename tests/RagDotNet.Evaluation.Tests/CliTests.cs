using static RagDotNet.Evaluation.Tests.TestData;

namespace RagDotNet.Evaluation.Tests;

public class CliTests
{
    private const string Header = "| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |";
    private const string FakeNote = "Scores from fake embeddings only check that the harness works; they are not meaningful.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await Program.RunAsync(args, stdout, stderr, TestContext.Current.CancellationToken);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string[] DataRows(string report) =>
        report.Split('\n').Where(l => l.StartsWith("| structure-aware ", StringComparison.Ordinal)).ToArray();

    [Fact] // Review Focus 3
    public async Task Fake_mode_report_is_byte_identical_across_runs()
    {
        var dir = SmallDataset();
        var first = await Run("--dataset", dir, "--max-tokens", "128,256", "--overlap", "0");
        var second = await Run("--dataset", dir, "--max-tokens", "128,256", "--overlap", "0");

        Assert.Equal(0, first.Code);
        Assert.Equal(first.Out, second.Out);
        Assert.StartsWith("# RagDotNet retrieval evaluation\n", first.Out, StringComparison.Ordinal);
        Assert.Contains(Header, first.Out, StringComparison.Ordinal);
        Assert.Equal(["| structure-aware | 128 | 0 | vector |", "| structure-aware | 256 | 0 | vector |"],
            DataRows(first.Out).Select(r => string.Join('|', r.Split('|').Take(5)) + "|"));
        Assert.Contains(FakeNote, first.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Date", first.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", first.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_lists_metadata_and_formats_scores_with_three_decimals()
    {
        var (code, report, _) = await Run("--dataset", SmallDataset(), "--max-tokens", "256", "--overlap", "0.1");

        Assert.Equal(0, code);
        Assert.Contains("- Embedder: fake (fake-embed)\n", report, StringComparison.Ordinal);
        Assert.Contains("- Chat model: none\n", report, StringComparison.Ordinal);
        Assert.Contains("- Corpus documents: 2\n", report, StringComparison.Ordinal);
        Assert.Contains("- Questions: 3\n", report, StringComparison.Ordinal);
        Assert.Matches(@"- Dataset hash: [0-9a-f]{12}\n", report);
        Assert.Matches(@"\| structure-aware \| 256 \| 0\.1 \| vector \| \d+ \| \d\.\d{3} \| \d\.\d{3} \| \d\.\d{3} \| \d\.\d{3} \|", report);
    }

    [Fact]
    public async Task Chat_modes_run_in_fake_mode_with_the_echo_client()
    {
        var (code, report, err) = await Run("--dataset", SmallDataset(), "--modes", "vector,multi-query,hyde", "--max-tokens", "256", "--overlap", "0");

        Assert.True(code == 0, err);
        Assert.Equal(3, DataRows(report).Length);
        Assert.Contains("| hyde |", report, StringComparison.Ordinal);
        Assert.DoesNotContain("- Chat model: none", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--max-tokens", "abc")]
    [InlineData("--max-tokens", "8")]
    [InlineData("--overlap", "0.5")]
    [InlineData("--modes", "bogus")]
    [InlineData("--embedder", "bogus")]
    [InlineData("--unknown", "x")]
    public async Task Bad_arguments_exit_1_with_a_message(string flag, string value)
    {
        var (code, report, err) = await Run("--dataset", SmallDataset(), flag, value);

        Assert.Equal(1, code);
        Assert.Equal("", report);
        Assert.Contains(flag, err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flag_without_a_value_exits_1()
    {
        var (code, _, err) = await Run("--max-tokens");
        Assert.Equal(1, code);
        Assert.Contains("--max-tokens", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_dataset_exits_1_and_names_the_question()
    {
        var dir = WriteDataset(new Dictionary<string, string> { ["a"] = "# A\n\nSome text lives here and nowhere else.\n" },
            """[ { "id": "bad-q", "question": "?", "documentId": "a", "evidence": "this evidence is missing entirely" } ]""");

        var (code, _, err) = await Run("--dataset", dir);

        Assert.Equal(1, code);
        Assert.Contains("bad-q: evidence not found in a", err, StringComparison.Ordinal);
    }

    [Fact] // Review Focus 4
    public async Task Unreachable_ollama_fails_fast_with_exit_code_2()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var (code, report, err) = await Run("--dataset", SmallDataset(), "--embedder", "ollama", "--ollama-url", $"http://127.0.0.1:{port}");

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), $"took {started.Elapsed}");
        Assert.Equal(2, code);
        Assert.Equal("", report);
        Assert.Contains($"Ollama is not reachable at http://127.0.0.1:{port}", err, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", err, StringComparison.Ordinal);
    }

    [Fact] // Review Focus 4: the server is up but the chat model was never pulled
    public async Task Missing_chat_model_exits_2_and_names_the_model()
    {
        await using var server = new FakeOllamaServer(["nomic-embed-text:latest"]);

        var (code, report, err) = await Run("--dataset", SmallDataset(), "--embedder", "ollama", "--ollama-url", server.Url,
            "--dimensions", "4", "--modes", "hyde", "--max-tokens", "256", "--overlap", "0");

        Assert.Equal(2, code);
        Assert.Equal("", report);
        Assert.Contains("ollama pull llama3.2:3b", err, StringComparison.Ordinal);
        Assert.DoesNotContain("not reachable", err, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_embed_model_is_not_reported_as_unreachable()
    {
        await using var server = new FakeOllamaServer(["llama3.2:3b"]);

        var (code, _, err) = await Run("--dataset", SmallDataset(), "--embedder", "ollama", "--ollama-url", server.Url,
            "--dimensions", "4", "--max-tokens", "256", "--overlap", "0");

        Assert.Equal(2, code);
        Assert.Contains("ollama pull nomic-embed-text", err, StringComparison.Ordinal);
        Assert.DoesNotContain("not reachable", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untagged_model_name_matches_latest_and_runs()
    {
        await using var server = new FakeOllamaServer(["nomic-embed-text:latest"]);

        var (code, report, err) = await Run("--dataset", SmallDataset(), "--embedder", "ollama", "--ollama-url", server.Url,
            "--dimensions", "4", "--max-tokens", "256", "--overlap", "0");

        Assert.True(code == 0, err);
        Assert.Single(DataRows(report));
    }

    [Fact]
    public async Task Provider_failure_mid_run_exits_2_with_one_line()
    {
        await using var server = new FakeOllamaServer(["nomic-embed-text:latest"], failEmbedAfter: 1);

        var (code, report, err) = await Run("--dataset", SmallDataset(), "--embedder", "ollama", "--ollama-url", server.Url,
            "--dimensions", "4", "--max-tokens", "256", "--overlap", "0");

        Assert.Equal(2, code);
        Assert.Equal("", report);
        Assert.Contains("failed during the run", err, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_exits_cleanly_instead_of_throwing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var code = await Program.RunAsync(["--dataset", SmallDataset()], stdout, stderr, cts.Token);

        Assert.Equal(Program.Cancelled, code);
        Assert.Contains("Cancelled", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_option_writes_the_report_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "ragdotnet-eval-" + Guid.NewGuid().ToString("N"), "report.md");
        var (code, report, _) = await Run("--dataset", SmallDataset(), "--max-tokens", "256", "--overlap", "0", "--output", path);

        Assert.Equal(0, code);
        Assert.Equal(report, await File.ReadAllTextAsync(path, Ct));
    }
}
