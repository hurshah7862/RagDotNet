using Microsoft.Extensions.AI;
using RagDotNet.Evaluation.Cli;
using RagDotNet.Evaluation.Datasets;
using RagDotNet.Evaluation.Providers;
using RagDotNet.Evaluation.Running;

namespace RagDotNet.Evaluation;

/// <summary>Entry point of the evaluation harness.</summary>
public static class Program
{
    /// <summary>Success.</summary>
    public const int Ok = 0;

    /// <summary>Bad arguments or an invalid dataset.</summary>
    public const int InvalidInput = 1;

    /// <summary>The provider is unreachable.</summary>
    public const int ProviderUnreachable = 2;

    /// <summary>Runs the harness on the console.</summary>
    public static Task<int> Main(string[] args) => RunAsync(args, Console.Out, Console.Error, CancellationToken.None);

    /// <summary>Runs the harness, writing the report to <paramref name="stdout"/> and errors to <paramref name="stderr"/>.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (OptionsException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            await stderr.WriteLineAsync(Options.Usage).ConfigureAwait(false);
            return InvalidInput;
        }

        Dataset dataset;
        try
        {
            dataset = await DatasetLoader.LoadAsync(options.DatasetDirectory, ct).ConfigureAwait(false);
        }
        catch (DatasetValidationException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return InvalidInput;
        }

        if (options.IsReal)
        {
            await stderr.WriteLineAsync("--embedder ollama is not available until Task 4.").ConfigureAwait(false);
            return InvalidInput;
        }

        using var embedder = new FakeEmbeddingGenerator(options.Dimensions);
        using IChatClient? chat = options.NeedsChat ? new EchoChatClient() : null;
        var meta = new ReportMetadata("fake", FakeEmbeddingGenerator.ModelId, chat is null ? "none" : EchoChatClient.ModelId, null, Fake: true);

        var configs = EvaluationConfig.Matrix(options.Chunkers, options.MaxTokens, options.Overlaps, options.Modes);
        var results = await new EvaluationRunner(embedder, options.Dimensions, chat).RunAsync(dataset, configs, ct).ConfigureAwait(false);
        var report = ReportWriter.Write(meta, dataset, results);

        await stdout.WriteAsync(report).ConfigureAwait(false);
        if (options.Output is { } path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, report, ct).ConfigureAwait(false);
        }

        return Ok;
    }
}
