using System.Linq.Expressions;
using System.Reflection;
using RagDotNet.Filtering;

namespace RagDotNet.Access;

/// <summary>Builds the store filter for reads: partition visibility, the scope's own filter and the caller's filter.</summary>
internal static class ReadFilters
{
    private static readonly PropertyInfo Indexer = typeof(Dictionary<string, object?>).GetProperty("Item")!;

    /// <summary>
    /// Scoped: (Partition == own || (includeCommon &amp;&amp; Partition == "")) AND user filter AND caller filter.
    /// Full access: caller filter only, or Partition != "" when the common area is excluded.
    /// <paramref name="includeCommon"/> is ignored when <paramref name="partitioned"/> is false: a single-tenant app
    /// has only the common area. <paramref name="matchesNothing"/> is true when the combined filter can never match.
    /// A non-null <paramref name="requireModelId"/> ANDs <c>EmbeddingModelId == requireModelId</c> into the result
    /// (reads during a reindex see only re-embedded chunks); it is returned alone when there is no other clause.
    /// </summary>
    public static Expression<Func<Dictionary<string, object?>, bool>>? Build(
        ResolvedScope scope, bool includeCommon, bool partitioned, RagFilter? callerFilter, MetadataSchema schema, out bool matchesNothing,
        string? requireModelId = null)
    {
        if (!partitioned) includeCommon = true;
        RagFilter? combined = (scope.UserFilter, callerFilter) switch
        {
            (null, var f) => f,
            (var u, null) => u,
            (var u, var f) => new RagFilter.AndFilter([u, f]),
        };
        var translated = RagFilterTranslator.Translate(combined, schema, out matchesNothing);
        if (matchesNothing) return null;

        var p = Expression.Parameter(typeof(Dictionary<string, object?>), "r");
        var partition = PartitionClause(scope, includeCommon, p);

        Expression? body = null;
        if (translated is not null)
            body = new ParameterRebinder(translated.Parameters[0], p).Visit(translated.Body);
        if (partition is not null)
            body = body is null ? partition : Expression.AndAlso(partition, body);
        if (requireModelId is not null)
        {
            var model = Expression.Equal(
                Expression.Convert(Expression.MakeIndex(p, Indexer, [Expression.Constant("EmbeddingModelId")]), typeof(string)),
                Expression.Constant(requireModelId, typeof(string)));
            body = body is null ? model : Expression.AndAlso(model, body);
        }
        return body is null ? null : Expression.Lambda<Func<Dictionary<string, object?>, bool>>(body, p);
    }

    private static Expression? PartitionClause(ResolvedScope scope, bool includeCommon, ParameterExpression p)
    {
        var field = Expression.Convert(Expression.MakeIndex(p, Indexer, [Expression.Constant("Partition")]), typeof(string));
        Expression Eq(string v) => Expression.Equal(field, Expression.Constant(v, typeof(string)));

        if (scope.IsFullAccess)
            return includeCommon ? null : Expression.NotEqual(field, Expression.Constant("", typeof(string)));
        if (scope.PartitionKey.Length == 0 || !includeCommon) return Eq(scope.PartitionKey);
        return Expression.OrElse(Eq(scope.PartitionKey), Eq(""));
    }

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
