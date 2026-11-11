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

    /// <summary>The provider is unreachable, lacks a model, or failed during the run.</summary>
    public const int ProviderUnreachable = 2;

    /// <summary>The run was cancelled, for example with Ctrl+C.</summary>
    public const int Cancelled = 130;

    /// <summary>Runs the harness on the console.</summary>
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the run stop at its next await instead of killing the process
            cts.Cancel();
        };
        return await RunAsync(args, Console.Out, Console.Error, cts.Token).ConfigureAwait(false);
    }

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

        try
        {
            return await RunCoreAsync(options, stdout, stderr, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return Cancelled;
        }
        catch (HttpRequestException ex) when (options.IsReal)
        {
            await stderr.WriteLineAsync($"Ollama request failed during the run: {OllamaProviders.Describe(ex)}").ConfigureAwait(false);
            return ProviderUnreachable;
        }
    }

    private static async Task<int> RunCoreAsync(Options options, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        IReadOnlyList<DatasetSet> sets;
        try
        {
            sets = await DatasetLoader.LoadSetsAsync(options.DatasetDirectory, options.Formats, ct).ConfigureAwait(false);
        }
        catch (DatasetValidationException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return InvalidInput;
        }

        if (sets.Count == 0)
        {
            // Every requested format was one no set has a rendered copy of; an empty report would look like success.
            await stderr.WriteLineAsync(
                $"No dataset set has the requested format(s) {string.Join(", ", options.Formats)} in {options.DatasetDirectory}.").ConfigureAwait(false);
            return InvalidInput;
        }

        IEmbeddingGenerator<string, Embedding<float>> embedder;
        IChatClient? chat;
        ReportMetadata meta;
        if (options.IsReal)
        {
            // Checking the pulled models first turns a typo in a model name into a clear message before any work starts.
            string[] models = options.NeedsChat ? [options.EmbedModel, options.ChatModel] : [options.EmbedModel];
            if (await OllamaProviders.CheckModelsAsync(options.OllamaUrl, models, ct).ConfigureAwait(false) is { } problem)
            {
                await stderr.WriteLineAsync(problem).ConfigureAwait(false);
                return ProviderUnreachable;
            }

            embedder = OllamaProviders.CreateEmbedder(options.OllamaUrl, options.EmbedModel);
            if (await OllamaProviders.PingAsync(embedder, options.Dimensions, ct).ConfigureAwait(false) is { } reason)
            {
                embedder.Dispose();
                await stderr.WriteLineAsync($"Ollama embedding check failed for '{options.EmbedModel}': {reason}").ConfigureAwait(false);
                return ProviderUnreachable;
            }

            chat = options.NeedsChat ? OllamaProviders.CreateChat(options.OllamaUrl, options.ChatModel) : null;
            meta = new ReportMetadata("ollama", options.EmbedModel, chat is null ? "none" : options.ChatModel,
                DateOnly.FromDateTime(DateTime.UtcNow), Fake: false);
        }
        else
        {
            embedder = new FakeEmbeddingGenerator(options.Dimensions);
            chat = options.NeedsChat ? new EchoChatClient() : null;
            meta = new ReportMetadata("fake", FakeEmbeddingGenerator.ModelId, chat is null ? "none" : EchoChatClient.ModelId, null, Fake: true);
        }

        using var ownedEmbedder = embedder;
        using var ownedChat = chat;

        var configs = EvaluationConfig.Matrix(options.Chunkers, options.MaxTokens, options.Overlaps, options.Modes);
        var results = await new EvaluationRunner(embedder, options.Dimensions, chat).RunAsync(sets, configs, ct).ConfigureAwait(false);
        var report = ReportWriter.Write(meta, sets, results);

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
