using Microsoft.Extensions.AI;
using RagDotNet.Configuration;
using RagDotNet.Diagnostics;

namespace RagDotNet.Retrieval;

/// <summary>Turns a question into the list of queries that are searched.</summary>
internal sealed class QueryPlanner(RagOptions ragOptions)
{
    internal const int MaxQueries = 10;

    public async Task<IReadOnlyList<string>> PlanAsync(string question, IReadOnlyList<ChatMessage> history, RetrievalOptions options, IChatClient? chat, CancellationToken ct)
    {
        IReadOnlyList<string> queries = Normalize([question], [question]);
        if (history.Count > 0)
        {
            queries = await RunAsync(new CondenseQueryTransformer(), new QueryContext(question, history, queries, chat), queries, ct).ConfigureAwait(false);
        }

        var transformers =options?.QueryTransformers ?? ragOptions.QueryTransformers.ToList();
        foreach (var transformer in transformers)
        {
            queries = await RunAsync(transformer, new QueryContext(question, history, queries, chat), queries, ct).ConfigureAwait(false);
        }

        return queries;
    }

    private async Task<IReadOnlyList<string>> RunAsync(IQueryTransformer transformer, QueryContext context, IReadOnlyList<string> previous, CancellationToken ct)
    {
        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.transform");
        activity?.SetTag("rag.transformer", transformer.GetType().Name);
        try
        {
            var output = await transformer.TransformAsync(context, ct).ConfigureAwait(false);
            var result = Normalize(output ?? [], previous);
            activity?.SetTag("rag.query.count", result.Count);
            if (activity is not null && ragOptions.EnableSensitiveData) activity.SetTag("rag.queries", string.Join("\n", result));
            return result;
        }
        catch (Exception ex)
        {
            RagTelemetry.Fail(activity, ex);
            throw;
        }
    }

    internal static IReadOnlyList<string> Normalize(IEnumerable<string?> queries, IReadOnlyList<string> previous)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queries)
        {
            var t = q?.Trim();
            if (string.IsNullOrEmpty(t) || !seen.Add(t)) continue;
            result.Add(t);
            if (result.Count == MaxQueries) break;
        }

        return result.Count > 0 ? result : previous;
    }
}
