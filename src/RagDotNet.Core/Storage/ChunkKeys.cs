using System.Security.Cryptography;
using System.Text;

namespace RagDotNet.Storage;

/// <summary>Deterministic record keys.</summary>
public static class ChunkKeys
{
    /// <summary>Builds the canonical partition key: sorted, URL-escaped <c>k=v</c> pairs joined with <c>&amp;</c>.</summary>
    /// <param name="partition">Partition values; null or empty means no partition.</param>
    /// <returns>The partition key, or an empty string.</returns>
    public static string Partition(IReadOnlyDictionary<string, string>? partition)
    {
        if (partition is null || partition.Count == 0) return "";
        return string.Join("&", partition
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>Builds the key of a chunk record.</summary>
    /// <param name="partitionKey">The partition key from <see cref="Partition"/>.</param>
    /// <param name="documentId">The document id.</param>
    /// <param name="chunkIndex">The chunk index.</param>
    /// <returns>The chunk key.</returns>
    public static string Chunk(string partitionKey, string documentId, int chunkIndex) =>
        Document(partitionKey, documentId) + ":" + chunkIndex;

    /// <summary>Builds the key of a document (manifest) record.</summary>
    /// <param name="partitionKey">The partition key from <see cref="Partition"/>.</param>
    /// <param name="documentId">The document id.</param>
    /// <returns>The document key.</returns>
    public static string Document(string partitionKey, string documentId) =>
        partitionKey == "" ? documentId : partitionKey + "|" + documentId;

    /// <summary>Derives a deterministic Guid (RFC 9562 version 8) from a string key.</summary>
    /// <param name="key">The string key.</param>
    /// <returns>The Guid.</returns>
    public static Guid ToGuid(string key)
    {
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16).ToArray();
        b[6] = (byte)((b[6] & 0x0F) | 0x80);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b, bigEndian: true);
    }
}
