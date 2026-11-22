using System.Linq.Expressions;
using System.Reflection;

namespace RagDotNet.Storage;

/// <summary>How store filters read a field of a dynamic record.</summary>
internal static class RecordFields
{
    private static readonly MethodInfo ItemGetter = typeof(Dictionary<string, object?>).GetProperty("Item")!.GetGetMethod()!;

    /// <summary>
    /// <c>(type)r["name"]</c>, as a call to the indexer's getter: the shape a C# lambda produces and every supported
    /// connector translates. (The PgVector connector does not translate an IndexExpression.)
    /// </summary>
    public static Expression Read(ParameterExpression record, string name, Type type) =>
        Expression.Convert(Expression.Call(record, ItemGetter, Expression.Constant(name)), type);
}
