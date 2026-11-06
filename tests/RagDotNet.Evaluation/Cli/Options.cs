using System.Globalization;
using RagDotNet.Chunking;
using RagDotNet.Evaluation.Running;

namespace RagDotNet.Evaluation.Cli;

/// <summary>A command-line argument is invalid.</summary>
public sealed class OptionsException(string message) : Exception(message);

/// <summary>Parsed command-line options.</summary>
public sealed record Options
{
    /// <summary>Usage text printed with argument errors.</summary>
    public const string Usage = """
        Usage: RagDotNet.Evaluation [options]
          --embedder fake|ollama      Embedding provider (default fake)
          --ollama-url <url>          Ollama endpoint (default http://localhost:11434)
          --embed-model <name>        Ollama embedding model (default nomic-embed-text)
          --chat-model <name>         Ollama chat model for multi-query/hyde (default llama3.2:3b)
          --dimensions <n>            Embedding dimensions (default 256 fake, 768 ollama)
          --modes <list>              vector,multi-query,hyde (default vector)
          --max-tokens <list>         Chunk sizes (default 128,256,512,1024)
          --overlap <list>            Overlap ratios in [0, 0.5) (default 0,0.1,0.2)
          --dataset <dir>             Dataset directory (default: Dataset next to the harness)
          --output <path>             Also write the report to this file
        """;

    /// <summary><c>fake</c> or <c>ollama</c>.</summary>
    public string Embedder { get; init; } = "fake";

    /// <summary>Ollama endpoint.</summary>
    public Uri OllamaUrl { get; init; } = new("http://localhost:11434");

    /// <summary>Ollama embedding model.</summary>
    public string EmbedModel { get; init; } = "nomic-embed-text";

    /// <summary>Ollama chat model.</summary>
    public string ChatModel { get; init; } = "llama3.2:3b";

    /// <summary>Embedding dimensions; defaults by embedder.</summary>
    public int Dimensions { get; init; }

    /// <summary>Retrieval modes.</summary>
    public IReadOnlyList<string> Modes { get; init; } = [EvaluationConfig.Vector];

    /// <summary>Chunk sizes.</summary>
    public IReadOnlyList<int> MaxTokens { get; init; } = [128, 256, 512, 1024];

    /// <summary>Overlap ratios.</summary>
    public IReadOnlyList<double> Overlaps { get; init; } = [0, 0.1, 0.2];

    /// <summary>Chunkers; only structure-aware exists in this stage.</summary>
    public IReadOnlyList<string> Chunkers { get; init; } = [RagDotNet.Chunking.Chunkers.StructureAware];

    /// <summary>Dataset directory.</summary>
    public string DatasetDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "Dataset");

    /// <summary>Report file, or null.</summary>
    public string? Output { get; init; }

    /// <summary>Whether real (Ollama) providers are used.</summary>
    public bool IsReal => Embedder == "ollama";

    /// <summary>Whether a selected mode needs a chat client.</summary>
    public bool NeedsChat => Modes.Any(m => m != EvaluationConfig.Vector);

    /// <summary>Parses the arguments.</summary>
    /// <exception cref="OptionsException">An argument is unknown, missing its value, or invalid.</exception>
    public static Options Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var o = new Options();
        int? dimensions = null;
        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new OptionsException(IsKnown(flag) ? $"{flag} needs a value." : $"Unknown option {flag}.");
            }

            var value = args[++i];
            o = flag switch
            {
                "--embedder" => o with { Embedder = value is "fake" or "ollama" ? value : throw Bad(flag, value, "expected fake or ollama") },
                "--ollama-url" => o with { OllamaUrl = Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : throw Bad(flag, value, "expected an http(s) URL") },
                "--embed-model" => o with { EmbedModel = NonEmpty(flag, value) },
                "--chat-model" => o with { ChatModel = NonEmpty(flag, value) },
                "--dimensions" => SetDimensions(o, ParseInt(flag, value, 1), out dimensions),
                "--modes" => o with { Modes = List(flag, value, m => EvaluationConfig.Modes.Contains(m) ? m : throw Bad(flag, m, "expected vector, multi-query or hyde")) },
                "--max-tokens" => o with { MaxTokens = List(flag, value, v => ParseInt(flag, v, 16)) },
                "--overlap" => o with { Overlaps = List(flag, value, v => ParseOverlap(flag, v)) },
                "--dataset" => o with { DatasetDirectory = NonEmpty(flag, value) },
                "--output" => o with { Output = NonEmpty(flag, value) },
                _ => throw new OptionsException($"Unknown option {flag}."),
            };
        }

        return o with { Dimensions = dimensions ?? (o.IsReal ? 768 : 256) };
    }

    private static readonly string[] Known =
        ["--embedder", "--ollama-url", "--embed-model", "--chat-model", "--dimensions", "--modes", "--max-tokens", "--overlap", "--dataset", "--output"];

    private static bool IsKnown(string flag) => Known.Contains(flag, StringComparer.Ordinal);

    private static Options SetDimensions(Options o, int value, out int? dimensions)
    {
        dimensions = value;
        return o;
    }

    private static OptionsException Bad(string flag, string value, string why) => new($"Invalid value '{value}' for {flag}: {why}.");

    private static string NonEmpty(string flag, string value) =>
        string.IsNullOrWhiteSpace(value) ? throw Bad(flag, value, "must not be empty") : value;

    private static int ParseInt(string flag, string value, int min) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min
            ? n
            : throw Bad(flag, value, $"expected a whole number of at least {min}");

    private static double ParseOverlap(string flag, string value) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d is >= 0 and < 0.5
            ? d
            : throw Bad(flag, value, "expected a number in [0, 0.5)");

    private static IReadOnlyList<T> List<T>(string flag, string value, Func<string, T> parse)
    {
        var items = value.Split(',', StringSplitOptions.TrimEntries);
        if (items.Any(s => s.Length == 0)) throw Bad(flag, value, "empty list item");
        var parsed = items.Select(parse).ToList();
        return parsed.Distinct().Count() == parsed.Count ? parsed : throw Bad(flag, value, "duplicate list item");
    }
}
