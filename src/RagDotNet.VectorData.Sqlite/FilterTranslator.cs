using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace RagDotNet.VectorData.Sqlite;

/// <summary>
/// Translates the filter shapes RagDotNet emits over dynamic records into a SQLite boolean. Every translated node is
/// two-valued (never NULL), so Not and NotEqual keep C# semantics for missing values.
/// </summary>
internal static class FilterTranslator
{
    public static (string Sql, IReadOnlyList<SqliteParameter> Parameters) Translate(LambdaExpression filter, Schema schema, string prefix)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var context = new Context(schema, filter.Parameters[0], prefix);
        return (context.Bool(filter.Body), context.Parameters);
    }

    /// <summary>The column a field access like <c>r["name"]</c> reads, or null when <paramref name="node"/> is not one.</summary>
    public static Column? Field(Expression node, ParameterExpression record, Schema schema)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs } u) node = u.Operand;
        Expression? argument = node switch
        {
            IndexExpression { Object: var o, Arguments: [var a] } when o == record => a,
            MethodCallExpression { Method.Name: "get_Item", Object: var o, Arguments: [var a] } when o == record => a,
            _ => null,
        };
        if (argument is null) return null;
        if (Uses(argument, record)) throw Unsupported("a field name computed from the record");
        var name = Evaluate(argument) as string ?? throw new NotSupportedException("RagDotNet.VectorData.Sqlite filters need a string field name.");
        var column = schema.Find(name) ?? throw new ArgumentException($"Filter field '{name}' is not a property of collection '{schema.Name}'.");
        if (column.Kind == ColumnKind.Vector) throw Unsupported($"filtering on the vector property '{name}'");
        return column;
    }

    private static NotSupportedException Unsupported(string what) =>
        new($"Filter node {what} is not supported by RagDotNet.VectorData.Sqlite.");

    private static object? Evaluate(Expression node) =>
        node is ConstantExpression c ? c.Value : Expression.Lambda(node).Compile().DynamicInvoke();

    private static bool Uses(Expression node, ParameterExpression p)
    {
        var finder = new ParameterFinder(p);
        finder.Visit(node);
        return finder.Found;
    }

    private sealed class ParameterFinder(ParameterExpression p) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == p) Found = true;
            return node;
        }
    }

    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    private sealed class Context(Schema schema, ParameterExpression record, string prefix)
    {
        public List<SqliteParameter> Parameters { get; } = [];

        public string Bool(Expression node)
        {
            switch (node)
            {
                case ConstantExpression { Value: bool b }:
                    return b ? "1" : "0";
                case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                    return $"({Bool(and.Left)} AND {Bool(and.Right)})";
                case BinaryExpression { NodeType: ExpressionType.OrElse } or:
                    return $"({Bool(or.Left)} OR {Bool(or.Right)})";
                case UnaryExpression { NodeType: ExpressionType.Not } not when not.Type == typeof(bool):
                    return $"(NOT {Bool(not.Operand)})";
                case BinaryExpression b when b.NodeType is ExpressionType.Equal or ExpressionType.NotEqual
                    or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual:
                    return Compare(b);
                case MethodCallExpression call when IsContains(call.Method):
                    return Contains(call);
                case MethodCallExpression call:
                    throw Unsupported($"'{call.Method.Name}'");
                default:
                    if (!Uses(node, record) && node.Type == typeof(bool)) return (bool)Evaluate(node)! ? "1" : "0";
                    throw Unsupported($"'{node.NodeType}'");
            }
        }

        private static bool IsContains(MethodInfo m) =>
            m.IsGenericMethod && m.GetGenericMethodDefinition() == EnumerableContains && m.GetGenericArguments()[0] == typeof(string);

        private string Contains(MethodCallExpression call)
        {
            var column = Field(call.Arguments[0], record, schema);
            if (column is null || column.Kind != ColumnKind.StringArray || Uses(call.Arguments[1], record))
                throw Unsupported("'Contains' other than on a string[] property with a value");
            var value = Evaluate(call.Arguments[1]);
            // json_each over NULL yields no rows, so a missing array contains nothing.
            return value is null ? "0" : $"EXISTS (SELECT 1 FROM json_each({column.Quoted}) WHERE value = {Add(value)})";
        }

        private string Compare(BinaryExpression b)
        {
            var (column, valueNode, op) = Field(b.Left, record, schema) is { } left ? (left, b.Right, b.NodeType)
                : Field(b.Right, record, schema) is { } right ? (right, b.Left, Flip(b.NodeType))
                : throw Unsupported($"'{b.NodeType}' without a record field");
            if (Uses(valueNode, record)) throw Unsupported($"'{b.NodeType}' between two record fields");
            var value = Evaluate(valueNode);

            if (op is ExpressionType.Equal or ExpressionType.NotEqual)
            {
                // IS and IS NOT are SQLite's null-safe equality: two-valued, the same answers as C# == and !=.
                if (value is null) return $"({column.Quoted} {(op == ExpressionType.Equal ? "IS NULL" : "IS NOT NULL")})";
                if (column.Kind == ColumnKind.StringArray) throw Unsupported($"'{op}' on the string[] property '{column.Name}'");
                return $"({column.Quoted} {(op == ExpressionType.Equal ? "IS" : "IS NOT")} {Add(column.ToDb(value))})";
            }

            if (column.Kind is not (ColumnKind.Int32 or ColumnKind.Int64 or ColumnKind.Date))
                throw Unsupported($"'{op}' on the {column.Type.Name} property '{column.Name}'");
            // C# lifted comparisons with null are false.
            if (value is null) return "0";
            var sql = op switch
            {
                ExpressionType.GreaterThan => ">",
                ExpressionType.GreaterThanOrEqual => ">=",
                ExpressionType.LessThan => "<",
                _ => "<=",
            };
            return $"({column.Quoted} IS NOT NULL AND {column.Quoted} {sql} {Add(column.ToDb(value))})";
        }

        private static ExpressionType Flip(ExpressionType t) => t switch
        {
            ExpressionType.GreaterThan => ExpressionType.LessThan,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
            ExpressionType.LessThan => ExpressionType.GreaterThan,
            ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
            _ => t,
        };

        private string Add(object value)
        {
            var name = $"@{prefix}{Parameters.Count}";
            Parameters.Add(new SqliteParameter(name, value));
            return name;
        }
    }
}
