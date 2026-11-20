using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using RagDotNet.Chunking;

namespace RagDotNet.Retrieval;

/// <summary>
/// An <see cref="IReranker"/> that asks the app's chat model to score each candidate from 0 to 10 against the question.
/// Candidates go in groups of <see cref="LlmRerankerOptions.BatchSize"/> per chat call, so one retrieval costs
/// <c>ceil(candidates / BatchSize)</c> calls. When the model's reply cannot be read even after one retry, retrieval keeps
/// the fused order and logs a warning. When the chat client reports reading far fewer input tokens than were sent, the
/// prompt was probably cut to fit the model's context window, and the reranker logs a warning once per call.
/// </summary>
public sealed class LlmReranker : IReranker
{
    private const string DefaultInstruction =
        "Score how well each passage answers the question, from 0 (does not help answer it) to 10 (answers it directly and completely).";

    private const string FormatInstruction =
        "Reply with only a JSON array of integers, one score from 0 to 10 per passage, in passage order, for example [7, 0, 10].";

    /// <summary>A model that reports reading less than this share of the tokens counted with the reranker's tokenizer probably had the prompt cut.</summary>
    private const double CutPromptRatio = 0.85;

    private readonly IChatClient _chat;
    private readonly ILogger _logger;
    private readonly LlmRerankerOptions _options;
    private readonly Tokenizer _tokenizer;

    /// <summary>Creates the reranker.</summary>
    /// <param name="chat">The chat client that scores the candidates.</param>
    /// <param name="options">The options; defaults when null.</param>
    /// <param name="tokenizer">Counts tokens for <see cref="LlmRerankerOptions.MaxCandidateTokens"/>; cl100k_base when null.</param>
    /// <param name="logger">Logs a warning when the model probably read a cut prompt; none when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="chat"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is below 1.</exception>
    public LlmReranker(IChatClient chat, LlmRerankerOptions? options = null, Tokenizer? tokenizer = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _options = options ?? new LlmRerankerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.BatchSize, 1, "BatchSize");
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxConcurrency, 1, "MaxConcurrency");
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxCandidateTokens, 1, "MaxCandidateTokens");
        _tokenizer = tokenizer ?? TokenCounter.CreateDefault();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || top < 1) return [];

        var batches = new List<(int Start, int Count)>();
        for (var start = 0; start < candidates.Count; start += _options.BatchSize)
            batches.Add((start, Math.Min(_options.BatchSize, candidates.Count - start)));

        var calls = 0;
        var warnedCut = 0;
        using var gate = new SemaphoreSlim(_options.MaxConcurrency);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scores = new double[candidates.Count];
        try
        {
            await Task.WhenAll(batches.Select(async batch =>
            {
                await gate.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    var batchScores = await ScoreBatchAsync(query, candidates, batch.Start, batch.Count, () => Interlocked.Increment(ref calls), () => Interlocked.Exchange(ref warnedCut, 1) == 0, cts.Token).ConfigureAwait(false);
                    batchScores.CopyTo(scores, batch.Start);
                }
                catch
                {
                    // One batch failing ends the call: the other batches stop instead of spending chat calls. They end
                    // canceled, not faulted, so Task.WhenAll reports this batch's exception.
                    await cts.CancelAsync().ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }
        finally
        {
            Activity.Current?.SetTag("rag.rerank.calls", calls);
        }

        // Highest score first; equal scores keep the fused order.
        return Enumerable.Range(0, candidates.Count)
            .OrderByDescending(i => scores[i])
            .Take(top)
            .Select(i => new RerankResult(i, scores[i]))
            .ToList();
    }

    private async Task<double[]> ScoreBatchAsync(
        string query, IReadOnlyList<RetrievedChunk> candidates, int start, int count, Action countCall, Func<bool> firstCutWarning, CancellationToken ct)
    {
        var system = (_options.Instructions ?? DefaultInstruction) + "\n" + FormatInstruction;
        var user = UserPrompt(query, candidates, start, count);
        var messages = new List<ChatMessage> { new(ChatRole.System, system), new(ChatRole.User, user) };
        long? sent = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            countCall();
            var response = await _chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            if (response.Usage?.InputTokenCount is { } read)
            {
                sent ??= _tokenizer.CountTokens(system) + _tokenizer.CountTokens(user);
                if (read < sent * CutPromptRatio && firstCutWarning())
                {
                    _logger.LogWarning(
                        "The chat model read {Read} of about {Sent} prompt tokens sent to rerank {Count} passages; the prompt was probably cut to fit "
                        + "the model's context window, so the scores may ignore the question. Lower LlmRerankerOptions.BatchSize or "
                        + "MaxCandidateTokens, or raise the model's context window (num_ctx in Ollama).",
                        read, sent, count);
                }
            }

            if (Parse(response.Text, count) is { } parsed) return parsed;
        }

        throw new RerankFallbackException($"the model's reply could not be read as {count} scores from 0 to 10, twice");
    }

    private string UserPrompt(string query, IReadOnlyList<RetrievedChunk> candidates, int start, int count)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").Append(query).Append("\n\nPassages:\n");
        for (var i = 0; i < count; i++)
            sb.Append('[').Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("] ")
                .Append(Truncate(candidates[start + i].Text).ReplaceLineEndings(" ")).Append("\n\n");
        return sb.ToString();
    }

    private string Truncate(string text)
    {
        if (text.Length == 0) return text;
        var end = _tokenizer.GetIndexByTokenCount(text, _options.MaxCandidateTokens, out var normalized, out _);
        return (normalized ?? text)[..end];
    }

    /// <summary>The scores divided by 10, or null when the reply holds no JSON array of exactly <paramref name="count"/> numbers from 0 to 10.</summary>
    internal static double[]? Parse(string? reply, int count)
    {
        if (reply is null) return null;
        var open = reply.IndexOf('[', StringComparison.Ordinal);
        var close = open < 0 ? -1 : reply.IndexOf(']', open);
        if (close < 0) return null;
        double[]? values;
        try
        {
            values = JsonSerializer.Deserialize<double[]>(reply.AsSpan(open, close - open + 1));
        }
        catch (JsonException)
        {
            return null;
        }

        if (values is null || values.Length != count || values.Any(v => !double.IsFinite(v) || v is < 0 or > 10)) return null;
        return values.Select(v => v / 10).ToArray();
    }
}
