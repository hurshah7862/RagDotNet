using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using RagDotNet.Access;
using RagDotNet.Diagnostics;
using RagDotNet.Documents;
using RagDotNet.Generation;
using RagDotNet.Ingestion;
using RagDotNet.Retrieval;

namespace RagDotNet.Pipeline;

internal sealed class RagPipeline(RagEngine engine, IAccessScope? accessScope = null, IChatClient? chatClient = null) : IRagPipeline
{
    internal RagEngine Engine => engine;

    public async Task<IngestResult> IngestAsync(DocumentSource source, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scope = await engine.ResolveScopeAsync(accessScope, cancellationToken).ConfigureAwait(false);
        return await engine.Ingestor.IngestAsync(source, scope, options, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IngestResult>> IngestManyAsync(IEnumerable<DocumentSource> sources, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in list)
        {
            ArgumentNullException.ThrowIfNull(s);
            if (!seen.Add(s.DocumentId))
                throw new ArgumentException($"Duplicate document id '{s.DocumentId}'.", nameof(sources));
        }

        var scope = await engine.ResolveScopeAsync(accessScope, cancellationToken).ConfigureAwait(false);
        var results = new IngestResult[list.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, list.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, engine.Options.IngestParallelism), CancellationToken = cancellationToken },
            async (i, ct) => results[i] = await engine.Ingestor.IngestAsync(list[i], scope, options, progress, ct).ConfigureAwait(false))
            .ConfigureAwait(false);
        return results;
    }

    public async Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var scope = await engine.ResolveScopeAsync(accessScope, cancellationToken).ConfigureAwait(false);
        return await engine.Ingestor.DeleteAsync(documentId, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var scope = await engine.ResolveScopeAsync(accessScope, cancellationToken).ConfigureAwait(false);
        return await engine.Retriever.RetrieveAsync([query], scope, options ?? new RetrievalOptions(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RagAnswer> AskAsync(string question, AskOptions? options = null, CancellationToken cancellationToken = default)
    {
        var chat = RequireChat(question, options);
        var (prompt, context, retrieved) = await PrepareAsync(question, options, cancellationToken).ConfigureAwait(false);
        if (prompt is null) return NotAnswered(retrieved);

        using var activity = StartGenerate(context);
        try
        {
            var response = await chat.GetResponseAsync(prompt, options?.ChatOptions, cancellationToken).ConfigureAwait(false);
            return Finish(activity, BuildAnswer(response.Text, response.Usage, context, retrieved));
        }
        catch (Exception ex)
        {
            RagTelemetry.Fail(activity, ex);
            throw;
        }
    }

    public IAsyncEnumerable<RagAnswerUpdate> AskStreamingAsync(string question, AskOptions? options = null, CancellationToken cancellationToken = default)
    {
        var chat = RequireChat(question, options);
        return StreamAsync(chat, question, options, cancellationToken);
    }

    private async IAsyncEnumerable<RagAnswerUpdate> StreamAsync(IChatClient chat, string question, AskOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (prompt, context, retrieved) = await PrepareAsync(question, options, cancellationToken).ConfigureAwait(false);
        if (prompt is null)
        {
            yield return new RagAnswerUpdate(null, NotAnswered(retrieved));
            yield break;
        }

        // The span covers the whole stream and ends on completion, failure or disposal by the consumer.
        using var activity = StartGenerate(context);
        var text = new StringBuilder();
        UsageDetails? usage = null;
        await using var enumerator = chat.GetStreamingResponseAsync(prompt, options?.ChatOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                update = enumerator.Current;
            }
            catch (Exception ex)
            {
                RagTelemetry.Fail(activity, ex);
                throw;
            }

            foreach (var content in update.Contents)
                if (content is UsageContent u) usage = u.Details;

            var delta = update.Text;
            if (string.IsNullOrEmpty(delta)) continue;
            text.Append(delta);
            yield return new RagAnswerUpdate(delta, null);
        }

        var answer = Finish(activity, BuildAnswer(text.ToString(), usage, context, retrieved));
        yield return new RagAnswerUpdate(null, answer);
    }

    private IChatClient RequireChat(string question, AskOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        if (options?.MaxContextTokens is <= 0)
            throw new ArgumentOutOfRangeException("MaxContextTokens", options.MaxContextTokens, "MaxContextTokens must be greater than zero.");
        return chatClient ?? throw new InvalidOperationException("No chat client configured. Call UseChatClient() with an IChatClient to use AskAsync.");
    }

    private async Task<(IList<ChatMessage>? Prompt, IReadOnlyList<RetrievedChunk> Context, IReadOnlyList<RetrievedChunk> Retrieved)> PrepareAsync(string question, AskOptions? options, CancellationToken ct)
    {
        var scope = await engine.ResolveScopeAsync(accessScope, ct).ConfigureAwait(false);
        var retrieved = await engine.Retriever.RetrieveAsync([question], scope, options?.Retrieval ?? new RetrievalOptions(), ct).ConfigureAwait(false);
        if (retrieved.Count == 0) return (null, [], retrieved);

        var budget = options?.MaxContextTokens ?? engine.Options.MaxContextTokens;
        var context = ContextBudget.Select(retrieved, budget, engine.Tokens);
        var builder = engine.Options.Prompt ?? new DefaultRagPromptBuilder();
        return (builder.Build(new RagPromptContext(question, context)), context, retrieved);
    }

    private static RagAnswer NotAnswered(IReadOnlyList<RetrievedChunk> retrieved)
    {
        RagTelemetry.AskUnanswered.Add(1);
        return new(DefaultRagPromptBuilder.NotAnsweredText, [], retrieved, null, false);
    }

    private Activity? StartGenerate(IReadOnlyList<RetrievedChunk> context)
    {
        var activity = RagTelemetry.ActivitySource.StartActivity("rag.generate");
        activity?.SetTag("gen_ai.operation.name", "chat");
        RagTelemetry.GenerateContextTokens.Record(context.Sum(c => engine.Tokens.Count(c.Text)));
        return activity;
    }

    private RagAnswer Finish(Activity? activity, RagAnswer answer)
    {
        if (activity is not null)
        {
            if (answer.Usage?.InputTokenCount is { } input) activity.SetTag("gen_ai.usage.input_tokens", input);
            if (answer.Usage?.OutputTokenCount is { } output) activity.SetTag("gen_ai.usage.output_tokens", output);
            if (engine.Options.EnableSensitiveData) activity.SetTag("rag.answer", answer.Text);
        }

        if (!answer.IsAnswered) RagTelemetry.AskUnanswered.Add(1);
        return answer;
    }

    private RagAnswer BuildAnswer(string text, UsageDetails? usage, IReadOnlyList<RetrievedChunk> context, IReadOnlyList<RetrievedChunk> retrieved)
    {
        var citations = CitationParser.Parse(text, context.Count)
            .Select(n => new Citation(n, context[n - 1], Locate(context[n - 1])))
            .ToList();
        var builder = engine.Options.Prompt ?? new DefaultRagPromptBuilder();
        return new RagAnswer(text, citations, retrieved, usage, !string.IsNullOrWhiteSpace(text) && !builder.IsUnanswered(text));
    }

    private static string Locate(RetrievedChunk c)
    {
        if (c.PageStart is { } start) return PageLocation.Format(start, c.PageEnd);
        return c.HeadingPath.Count > 0 ? string.Join(" > ", c.HeadingPath) : c.SourceName;
    }
}
