using System.Text.RegularExpressions;

namespace RagDotNet.Filtering;

/// <summary>Declares the custom metadata fields that documents may carry and filters may reference.</summary>
public sealed partial class MetadataSchema
{
    private static readonly HashSet<Type> Allowed =
        [typeof(string), typeof(int), typeof(long), typeof(bool), typeof(DateTimeOffset), typeof(string[])];

    private readonly Dictionary<string, Type> _fields = new(StringComparer.Ordinal);

    /// <summary>The declared fields and their types.</summary>
    public IReadOnlyDictionary<string, Type> Fields => _fields;

    /// <summary>Declares a metadata field.</summary>
    /// <typeparam name="T">One of string, int, long, bool, DateTimeOffset, string[].</typeparam>
    /// <param name="name">Field name; letters, digits and underscore, starting with a letter. Case-sensitive.</param>
    /// <returns>This schema.</returns>
    /// <exception cref="ArgumentException">The type or name is invalid, the name is reserved ("DocumentId"), or the name is already declared.</exception>
    public MetadataSchema Add<T>(string name)
    {
        if (!Allowed.Contains(typeof(T)))
            throw new ArgumentException($"Metadata type '{typeof(T)}' is not supported. Use string, int, long, bool, DateTimeOffset or string[].");
        if (name is null || !NameRegex().IsMatch(name))
            throw new ArgumentException($"Metadata field name '{name}' is invalid. Names must match ^[A-Za-z][A-Za-z0-9_]*$.", nameof(name));
        if (name == "DocumentId")
            throw new ArgumentException("Metadata field name 'DocumentId' is reserved; filter on the document id with RagFilter.Eq(\"DocumentId\", ...).", nameof(name));
        if (!_fields.TryAdd(name, typeof(T)))
            throw new ArgumentException($"Metadata field '{name}' is already declared.", nameof(name));
        return this;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex NameRegex();
}
