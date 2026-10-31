using RagDotNet.Filtering;

namespace RagDotNet.Access;

internal sealed record ResolvedScope(IReadOnlyDictionary<string, string> Partition, string PartitionKey, RagFilter? Filter)
{
    public static ResolvedScope None { get; } = new(new Dictionary<string, string>(), "", null);
}
