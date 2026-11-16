using RagDotNet.Filtering;
using RagDotNet.Keyword;

namespace RagDotNet.Access;

/// <summary>Builds the <see cref="KeywordFilter"/> for a keyword search, mirroring <see cref="ReadFilters.Build"/> clause for clause.</summary>
internal static class KeywordFilters
{
    /// <summary>
    /// Scoped: partitions [own, ""] (or [own] without the common area, or when the own partition is the common area).
    /// Full access: any partition, excluding the common area when <paramref name="includeCommon"/> is false.
    /// <paramref name="includeCommon"/> is ignored when <paramref name="partitioned"/> is false. The metadata condition is the
    /// scope's filter AND the caller's, simplified, with every value converted to its declared type. Returns null with
    /// <paramref name="matchesNothing"/> true when the condition can never match.
    /// </summary>
    /// <exception cref="ArgumentException">The filter is invalid for the schema.</exception>
    public static KeywordFilter? Build(
        ResolvedScope scope, bool includeCommon, bool partitioned, RagFilter? callerFilter, MetadataSchema schema, out bool matchesNothing)
    {
        if (!partitioned) includeCommon = true;
        matchesNothing = false;
        RagFilter? metadata = null;
        if (ReadFilters.Combine(scope, callerFilter) is { } combined)
        {
            var simplified = RagFilterTranslator.Simplify(combined, schema);
            if (simplified is null)
            {
                matchesNothing = true;
                return null;
            }

            metadata = Convert(simplified, schema);
        }

        if (scope.IsFullAccess) return new KeywordFilter(null, !includeCommon, metadata);
        IReadOnlyList<string> partitions = scope.PartitionKey.Length == 0 || !includeCommon ? [scope.PartitionKey] : [scope.PartitionKey, ""];
        return new KeywordFilter(partitions, false, metadata);
    }

    private static RagFilter Convert(RagFilter filter, MetadataSchema schema) => filter switch
    {
        RagFilter.EqFilter eq => eq with { Value = Value(eq.Field, eq.Value, schema) },
        RagFilter.InFilter i => i with { Values = i.Values.Select(v => Value(i.Field, v, schema)).ToArray() },
        RagFilter.AndFilter and => new RagFilter.AndFilter(and.Filters.Select(f => Convert(f, schema)).ToArray()),
        RagFilter.OrFilter or => new RagFilter.OrFilter(or.Filters.Select(f => Convert(f, schema)).ToArray()),
        _ => filter, // Contains and ContainsAny compare strings.
    };

    private static object Value(string field, object value, MetadataSchema schema) =>
        RagFilterTranslator.ConvertValue(value, RagFilterTranslator.FieldType(field, schema), field);
}
