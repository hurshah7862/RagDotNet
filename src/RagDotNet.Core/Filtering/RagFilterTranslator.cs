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

    /// <summary>Converts a filter value to the field's type without loss; anything inexact or ambiguous is rejected.</summary>
    private static object ConvertValue(object? value, Type type, string field)
    {
        if (value is null) throw new ArgumentException($"Filter value for '{field}' must not be null.");
        if (type.IsInstanceOfType(value)) return value;

        object? converted = null;
        if (type == typeof(long))
        {
            converted = ToWholeInt64(value);
        }
        else if (type == typeof(int))
        {
            if (ToWholeInt64(value) is { } w && w >= int.MinValue && w <= int.MaxValue) converted = (int)w;
        }
        else if (type == typeof(DateTimeOffset))
        {
            converted = value switch
            {
                DateTime { Kind: DateTimeKind.Utc or DateTimeKind.Local } dt => new DateTimeOffset(dt),
                string s when HasExplicitOffset(s)
                    && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
                _ => null,
            };
        }

        return converted ?? throw new ArgumentException(
            $"Filter value of type {value.GetType().Name} for field '{field}' cannot be converted exactly to {type.Name}."
            + (type == typeof(DateTimeOffset) ? " Use a DateTimeOffset, a UTC or local DateTime, or an ISO-8601 string with an explicit offset." : ""));
    }

    /// <summary>The value as a long when it is numeric, whole and in range; otherwise null.</summary>
    private static long? ToWholeInt64(object value)
    {
        const double TwoPow63 = 9223372036854775808.0;
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case ulong u:
                return u <= long.MaxValue ? (long)u : null;
            case decimal m:
                return decimal.Truncate(m) == m && m >= long.MinValue && m <= long.MaxValue ? (long)m : null;
            case double or float:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return Math.Truncate(d) == d && d >= -TwoPow63 && d < TwoPow63 ? (long)d : null; // NaN and infinities fail
            default:
                return null;
        }
    }

    /// <summary>True when an ISO-8601 date-time string ends in Z or a ±hh:mm offset.</summary>
    private static bool HasExplicitOffset(string s)
    {
        var t = s.Trim();
        var timeAt = t.IndexOf('T', StringComparison.OrdinalIgnoreCase);
        if (timeAt < 0) return false;
        if (t.EndsWith('Z') || t.EndsWith('z')) return true;
        return t.LastIndexOfAny(['+', '-']) > timeAt;
    }
}
