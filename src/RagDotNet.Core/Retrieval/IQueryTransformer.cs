namespace RagDotNet.Retrieval;

/// <summary>Turns the queries of a question into the queries that are searched, for example by adding alternatives.</summary>
public interface IQueryTransformer
{
    /// <summary>Returns the queries to search. Blank and duplicate queries are removed, and the list is capped at 10. An empty result keeps the previous queries.</summary>
    /// <param name="context">The question, the conversation so far and the queries produced by the previous step.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The new queries.</returns>
    Task<IReadOnlyList<string>> TransformAsync(QueryContext context, CancellationToken cancellationToken);
}
