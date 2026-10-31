using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace RagDotNet.Filtering;

/// <summary>Translates <see cref="RagFilter"/> trees into store filter expressions over dynamic records.</summary>
internal static class RagFilterTranslator
{
    private static readonly PropertyInfo Indexer = typeof(Dictionary<string, object?>).GetProperty("Item")!;

    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(string));

    /// <summary>
    /// Translates a filter. Returns null with <paramref name="matchesNothing"/> false when there is no filter;
    /// returns null with <paramref name="matchesNothing"/> true when the filter can never match, in which case
    /// callers must return no results without querying the store.
    /// </summary>
    public static Expression<Func<Dictionary<string, object?>, bool>>? Translate(RagFilter? filter, MetadataSchema schema, out bool matchesNothing)
    {
        matchesNothing = false;
        if (filter is null) return null;

        var simplified = Simplify(filter, schema);
        if (simplified is null)
        {
            matchesNothing = true;
            return null;
        }

        var p = Expression.Parameter(typeof(Dictionary<string, object?>), "r");
        return Expression.Lambda<Func<Dictionary<string, object?>, bool>>(Build(simplified, schema, p), p);
    }

    /// <summary>Prefix of storage property names for declared metadata fields.</summary>
    public const string MetaPrefix = "meta_";

    public static string StorageName(string field, MetadataSchema schema)
    {
        if (field == "DocumentId") return "DocumentId";
        if (field is not null && schema.Fields.ContainsKey(field)) return MetaPrefix + field;
        throw new ArgumentException($"Unknown filter field '{field}'. Declare it with RagOptions.Metadata.Add<T>().");
    }

    private static Type FieldType(string field, MetadataSchema schema) =>
        StorageName(field, schema) == "DocumentId" ? typeof(string) : schema.Fields[field];

    // Returns null when the filter is False (matches nothing). Every branch is validated even if False.
    private static RagFilter? Simplify(RagFilter filter, MetadataSchema schema)
    {
        switch (filter)
        {
            case RagFilter.EqFilter eq:
                BuildEqValueCheck(eq.Field, eq.Value, schema);
                return eq;
            case RagFilter.InFilter i:
                foreach (var v in i.Values) BuildEqValueCheck(i.Field, v, schema);
                FieldType(i.Field, schema);
                return i.Values.Count == 0 ? null : i;
            case RagFilter.ContainsFilter c:
                RequireArray(c.Field, schema);
                return c;
            case RagFilter.ContainsAnyFilter ca:
                RequireArray(ca.Field, schema);
                return ca.Values.Count == 0 ? null : ca;
            case RagFilter.AndFilter and:
            {
                if (and.Filters.Count == 0) throw new ArgumentException("And requires at least one filter.");
                var parts = new List<RagFilter>();
                var isFalse = false;
                foreach (var f in and.Filters)
                {
                    var s = Simplify(f, schema);
                    if (s is null) isFalse = true; else parts.Add(s);
                }
                return isFalse ? null : new RagFilter.AndFilter(parts);
            }
            case RagFilter.OrFilter or:
            {
                var parts = new List<RagFilter>();
                foreach (var f in or.Filters)
                {
                    var s = Simplify(f, schema);
                    if (s is not null) parts.Add(s);
                }
                return parts.Count == 0 ? null : new RagFilter.OrFilter(parts);
            }
            default:
                throw new ArgumentException($"Unsupported filter type '{filter.GetType().Name}'.");
        }
    }

    private static void BuildEqValueCheck(string field, object value, MetadataSchema schema)
    {
        var type = FieldType(field, schema);
        if (type == typeof(string[]))
            throw new ArgumentException($"Eq/In cannot be used on string[] field '{field}'. Use Contains or ContainsAny.");
        ConvertValue(value, type, field);
    }

    private static void RequireArray(string field, MetadataSchema schema)
    {
        var type = FieldType(field, schema);
        if (type != typeof(string[]))
            throw new ArgumentException($"Contains requires a string[] field, but '{field}' is {type.Name}.");
    }

    private static Expression Build(RagFilter filter, MetadataSchema schema, ParameterExpression p) => filter switch
    {
        RagFilter.EqFilter eq => BuildEq(eq.Field, eq.Value, schema, p),
        RagFilter.InFilter i => i.Values.Select(v => BuildEq(i.Field, v, schema, p)).Aggregate(Expression.OrElse),
        RagFilter.ContainsFilter c => BuildContains(c.Field, c.Value, schema, p),
        RagFilter.ContainsAnyFilter ca => ca.Values.Select(v => BuildContains(ca.Field, v, schema, p)).Aggregate(Expression.OrElse),
        RagFilter.AndFilter and => and.Filters.Select(f => Build(f, schema, p)).Aggregate(Expression.AndAlso),
        RagFilter.OrFilter or => or.Filters.Select(f => Build(f, schema, p)).Aggregate(Expression.OrElse),
        _ => throw new ArgumentException($"Unsupported filter type '{filter.GetType().Name}'."),
    };

    private static Expression Field(string field, Type type, MetadataSchema schema, ParameterExpression p) =>
        Expression.Convert(Expression.MakeIndex(p, Indexer, [Expression.Constant(StorageName(field, schema))]), type);

    private static Expression BuildEq(string field, object value, MetadataSchema schema, ParameterExpression p)
    {
        var type = FieldType(field, schema);
        var converted = ConvertValue(value, type, field);
        return Expression.Equal(Field(field, type, schema, p), Expression.Constant(converted, type));
    }

    private static Expression BuildContains(string field, string value, MetadataSchema schema, ParameterExpression p)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Expression.Call(EnumerableContains, Field(field, typeof(string[]), schema, p), Expression.Constant(value, typeof(string)));
    }

    private static object ConvertValue(object? value, Type type, string field)
    {
        if (value is null) throw new ArgumentException($"Filter value for '{field}' must not be null.");
        try
        {
            if (type.IsInstanceOfType(value)) return value;
            if (type == typeof(DateTimeOffset))
            {
                return value switch
                {
                    DateTime dt => new DateTimeOffset(dt),
                    string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture),
                    _ => throw new InvalidCastException(),
                };
            }
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException($"Cannot convert filter value '{value}' for field '{field}' to {type.Name}.", e);
        }
    }
}
