namespace RagDotNet.Filtering;

/// <summary>A store-independent filter over document metadata.</summary>
public abstract record RagFilter
{
    private protected RagFilter() { }

    /// <summary>Field equals value.</summary>
    /// <param name="field">Field name.</param>
    /// <param name="value">Value to compare with.</param>
    /// <returns>The filter.</returns>
    public static RagFilter Eq(string field, object value) => new EqFilter(field, value);

    /// <summary>Field equals any of the values. An empty list matches nothing.</summary>
    /// <param name="field">Field name.</param>
    /// <param name="values">Accepted values.</param>
    /// <returns>The filter.</returns>
    public static RagFilter In(string field, IEnumerable<object> values) => new InFilter(field, values.ToArray());

    /// <summary>String-array field contains the value.</summary>
    /// <param name="field">Field name.</param>
    /// <param name="value">Value to look for.</param>
    /// <returns>The filter.</returns>
    public static RagFilter Contains(string field, string value) => new ContainsFilter(field, value);

    /// <summary>String-array field contains any of the values. An empty list matches nothing.</summary>
    /// <param name="field">Field name.</param>
    /// <param name="values">Values to look for.</param>
    /// <returns>The filter.</returns>
    public static RagFilter ContainsAny(string field, IEnumerable<string> values) => new ContainsAnyFilter(field, values.ToArray());

    /// <summary>All filters must match.</summary>
    /// <param name="filters">Operands.</param>
    /// <returns>The filter.</returns>
    public static RagFilter And(params RagFilter[] filters) => new AndFilter(filters);

    /// <summary>Any filter may match.</summary>
    /// <param name="filters">Operands.</param>
    /// <returns>The filter.</returns>
    public static RagFilter Or(params RagFilter[] filters) => new OrFilter(filters);

    /// <summary>Combines two filters with AND.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The combined filter.</returns>
    public static RagFilter operator &(RagFilter left, RagFilter right) => new AndFilter([left, right]);

    /// <summary>Combines two filters with OR.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The combined filter.</returns>
    public static RagFilter operator |(RagFilter left, RagFilter right) => new OrFilter([left, right]);

    /// <summary>Equality filter.</summary>
    /// <param name="Field">Field name.</param>
    /// <param name="Value">Value.</param>
    public sealed record EqFilter(string Field, object Value) : RagFilter;

    /// <summary>Membership filter.</summary>
    /// <param name="Field">Field name.</param>
    /// <param name="Values">Accepted values.</param>
    public sealed record InFilter(string Field, IReadOnlyList<object> Values) : RagFilter;

    /// <summary>Array-contains filter.</summary>
    /// <param name="Field">Field name.</param>
    /// <param name="Value">Value.</param>
    public sealed record ContainsFilter(string Field, string Value) : RagFilter;

    /// <summary>Array-contains-any filter.</summary>
    /// <param name="Field">Field name.</param>
    /// <param name="Values">Values.</param>
    public sealed record ContainsAnyFilter(string Field, IReadOnlyList<string> Values) : RagFilter;

    /// <summary>Conjunction.</summary>
    /// <param name="Filters">Operands.</param>
    public sealed record AndFilter(IReadOnlyList<RagFilter> Filters) : RagFilter;

    /// <summary>Disjunction.</summary>
    /// <param name="Filters">Operands.</param>
    public sealed record OrFilter(IReadOnlyList<RagFilter> Filters) : RagFilter;
}
