using System.Globalization;
using Lucene.Net.Index;
using Lucene.Net.Search;
using RagDotNet.Filtering;

namespace RagDotNet.Keyword.Lucene;

/// <summary>Field names, value encoding and the translation of <see cref="KeywordFilter"/> into Lucene queries.</summary>
internal static class LuceneFilterTranslator
{
    public const string KeyField = "key";
    public const string DocumentField = "doc";
    public const string PartitionField = "part";
    public const string TextFieldName = "text";
    public const string MetaPrefix = "m_";

    /// <summary>Partition values are prefixed so the common area ("") is a real term.</summary>
    public static string PartitionTerm(string partition) => "p:" + partition;

    /// <summary>A metadata value as an indexed term: invariant text, DateTimeOffset as UTC ticks, bool as true/false.</summary>
    public static string Encode(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        DateTimeOffset d => d.UtcTicks.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>The filter as a query that must match, or null when there is no constraint.</summary>
    public static Query? Translate(KeywordFilter filter)
    {
        var clauses = new BooleanQuery();
        if (filter.Partitions is { } partitions)
            clauses.Add(AnyOf(partitions.Select(p => new TermQuery(new Term(PartitionField, PartitionTerm(p))))), Occur.MUST);
        if (filter.ExcludeCommon)
        {
            // A purely negative query matches nothing, so pair it with match-all.
            clauses.Add(new MatchAllDocsQuery(), Occur.MUST);
            clauses.Add(new TermQuery(new Term(PartitionField, PartitionTerm(""))), Occur.MUST_NOT);
        }

        if (filter.Metadata is { } metadata) clauses.Add(Translate(metadata), Occur.MUST);
        return clauses.Clauses.Count == 0 ? null : clauses;
    }

    private static Query Translate(RagFilter filter) => filter switch
    {
        RagFilter.EqFilter eq => Term(eq.Field, eq.Value),
        RagFilter.InFilter i => AnyOf(i.Values.Select(v => Term(i.Field, v))),
        RagFilter.ContainsFilter c => Term(c.Field, c.Value),
        RagFilter.ContainsAnyFilter ca => AnyOf(ca.Values.Select(v => Term(ca.Field, v))),
        RagFilter.AndFilter and => AllOf(and.Filters.Select(Translate)),
        RagFilter.OrFilter or => AnyOf(or.Filters.Select(Translate)),
        _ => throw new ArgumentException($"Unsupported filter type '{filter.GetType().Name}'."),
    };

    private static TermQuery Term(string field, object value) => field == "DocumentId"
        ? new TermQuery(new Term(DocumentField, (string)value))
        : new TermQuery(new Term(MetaPrefix + field, Encode(value)));

    private static BooleanQuery AnyOf(IEnumerable<Query> queries)
    {
        var q = new BooleanQuery();
        foreach (var part in queries) q.Add(part, Occur.SHOULD);
        q.MinimumNumberShouldMatch = 1;
        return q;
    }

    private static BooleanQuery AllOf(IEnumerable<Query> queries)
    {
        var q = new BooleanQuery();
        foreach (var part in queries) q.Add(part, Occur.MUST);
        return q;
    }
}
