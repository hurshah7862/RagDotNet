namespace RagDotNet.Filtering;

/// <summary>
/// Evaluates a <see cref="RagFilter"/> in memory against one document's stored metadata, with the same
/// validation and value conversion as <see cref="RagFilterTranslator"/>.
/// </summary>
internal static class RagFilterEvaluator
{
    /// <summary>
    /// True when the document matches. A null filter matches everything. A null or missing field value never
    /// matches <c>Eq</c>, <c>In</c>, <c>Contains</c> or <c>ContainsAny</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The filter is invalid for the schema.</exception>
    public static bool Matches(RagFilter? filter, MetadataSchema schema, string documentId, IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(metadata);
        if (filter is null) return true;

        // Validates the whole tree first; null means the filter can never match.
        var simplified = RagFilterTranslator.Simplify(filter, schema);
        return simplified is not null && Evaluate(simplified, schema, documentId, metadata);
    }

    /// <summary>
    /// True when the document matches a filter that was already simplified and whose values were already converted to the
    /// declared types (as in <see cref="Keyword.KeywordFilter.Metadata"/>), so no schema is needed. The field
    /// <c>DocumentId</c> means <paramref name="documentId"/>. Null or missing values never match.
    /// </summary>
    public static bool MatchesConverted(RagFilter? filter, string documentId, IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return filter switch
        {
            null => true,
            RagFilter.EqFilter eq => EqualsConverted(eq.Field, eq.Value, documentId, metadata),
            RagFilter.InFilter i => i.Values.Any(v => EqualsConverted(i.Field, v, documentId, metadata)),
            RagFilter.ContainsFilter c => ConvertedActual(c.Field, documentId, metadata) is string[] a && a.Contains(c.Value, StringComparer.Ordinal),
            RagFilter.ContainsAnyFilter ca => ConvertedActual(ca.Field, documentId, metadata) is string[] a && ca.Values.Any(v => a.Contains(v, StringComparer.Ordinal)),
            RagFilter.AndFilter and => and.Filters.All(f => MatchesConverted(f, documentId, metadata)),
            RagFilter.OrFilter or => or.Filters.Any(f => MatchesConverted(f, documentId, metadata)),
            _ => throw new ArgumentException($"Unsupported filter type '{filter.GetType().Name}'."),
        };
    }

    private static object? ConvertedActual(string field, string documentId, IReadOnlyDictionary<string, object?> metadata) =>
        field == "DocumentId" ? documentId : metadata.TryGetValue(field, out var v) ? v : null;

    private static bool EqualsConverted(string field, object value, string documentId, IReadOnlyDictionary<string, object?> metadata) =>
        ConvertedActual(field, documentId, metadata) is { } actual && value.Equals(actual);

    private static bool Evaluate(RagFilter filter, MetadataSchema schema, string documentId, IReadOnlyDictionary<string, object?> metadata) => filter switch
    {
        RagFilter.EqFilter eq => EqualsValue(eq.Field, eq.Value, schema, documentId, metadata),
        RagFilter.InFilter i => i.Values.Any(v => EqualsValue(i.Field, v, schema, documentId, metadata)),
        RagFilter.ContainsFilter c => ContainsValue(c.Field, c.Value, documentId, schema, metadata),
        RagFilter.ContainsAnyFilter ca => ca.Values.Any(v => ContainsValue(ca.Field, v, documentId, schema, metadata)),
        RagFilter.AndFilter and => and.Filters.All(f => Evaluate(f, schema, documentId, metadata)),
        RagFilter.OrFilter or => or.Filters.Any(f => Evaluate(f, schema, documentId, metadata)),
        _ => throw new ArgumentException($"Unsupported filter type '{filter.GetType().Name}'."),
    };

    private static object? Actual(string field, MetadataSchema schema, string documentId, IReadOnlyDictionary<string, object?> metadata) =>
        RagFilterTranslator.StorageName(field, schema) == "DocumentId"
            ? documentId
            : metadata.TryGetValue(field, out var v) ? v : null;

    private static bool EqualsValue(string field, object value, MetadataSchema schema, string documentId, IReadOnlyDictionary<string, object?> metadata)
    {
        var converted = RagFilterTranslator.ConvertValue(value, RagFilterTranslator.FieldType(field, schema), field);
        var actual = Actual(field, schema, documentId, metadata);
        return actual is not null && converted.Equals(actual);
    }

    private static bool ContainsValue(string field, string value, string documentId, MetadataSchema schema, IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Actual(field, schema, documentId, metadata) is string[] values && values.Contains(value, StringComparer.Ordinal);
    }
}
