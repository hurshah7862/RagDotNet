using RagDotNet.Filtering;

namespace RagDotNet.Keyword;

/// <summary>
/// The constraint a keyword search must apply, the same one a vector search gets. Built by RagDotNet from the access
/// scope and the caller's filter.
/// </summary>
/// <param name="Partitions">The partition keys a hit may have; null allows any. The common area is the empty string.</param>
/// <param name="ExcludeCommon">True to exclude the common area (partition "").</param>
/// <param name="Metadata">
/// The metadata condition, already validated against the schema, with every value converted to its declared type.
/// A chunk without a value for a field never matches a condition on that field. The field <c>DocumentId</c> means the chunk's document id.
/// </param>
public sealed record KeywordFilter(IReadOnlyList<string>? Partitions, bool ExcludeCommon, RagFilter? Metadata)
{
    /// <summary>No constraint.</summary>
    public static KeywordFilter All { get; } = new(null, false, null);
}
