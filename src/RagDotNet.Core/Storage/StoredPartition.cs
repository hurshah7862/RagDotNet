namespace RagDotNet.Storage;

/// <summary>
/// The partition value as stored. The common area's partition key is empty, but some stores (Redis) cannot query an
/// empty string in an indexed field, so it is stored as <see cref="Common"/>. A real partition key always contains
/// <c>=</c> (it is built from <c>k=v</c> pairs), so it never equals <see cref="Common"/>. Record keys keep the empty
/// partition key.
/// </summary>
internal static class StoredPartition
{
    public const string Common = "~common";

    public static string ToStore(string partitionKey) => partitionKey.Length == 0 ? Common : partitionKey;

    public static string FromStore(string stored) => stored == Common ? "" : stored;
}
