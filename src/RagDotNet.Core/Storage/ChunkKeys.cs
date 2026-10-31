using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RagDotNet.Storage;

/// <summary>Deterministic record keys.</summary>
public static class ChunkKeys
{
    /// <summary>Builds the canonical partition key: sorted, URL-escaped <c>k=v</c> pairs joined with <c>&amp;</c>.</summary>
    /// <param name="partition">Partition values; null or empty means no partition.</param>
    /// <returns>The partition key, or an empty string.</returns>
    /// <exception cref="ArgumentException">A key is null or empty, or a key or value is null or not valid UTF-16.</exception>
    public static string Partition(IReadOnlyDictionary<string, string>? partition)
    {
        if (partition is null || partition.Count == 0) return "";
        foreach (var (k, v) in partition)
        {
            if (string.IsNullOrEmpty(k)) throw new ArgumentException("Partition keys must not be null or empty.", nameof(partition));
            if (v is null) throw new ArgumentException($"Partition value for '{k}' must not be null.", nameof(partition));
            RequireWellFormed(k, nameof(partition));
            RequireWellFormed(v, nameof(partition));
        }

        return string.Join("&", partition
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>Builds the key of a chunk record. The document id is URL-escaped inside the key.</summary>
    /// <param name="partitionKey">The partition key from <see cref="Partition"/>.</param>
    /// <param name="documentId">The document id.</param>
    /// <param name="chunkIndex">The chunk index; not negative.</param>
    /// <returns>The chunk key.</returns>
    /// <exception cref="ArgumentException">The document id is null, empty or not valid UTF-16.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkIndex"/> is negative.</exception>
    public static string Chunk(string partitionKey, string documentId, int chunkIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        return Document(partitionKey, documentId) + ":" + chunkIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Builds the key of a document (manifest) record. The document id is URL-escaped inside the key.</summary>
    /// <param name="partitionKey">The partition key from <see cref="Partition"/>.</param>
    /// <param name="documentId">The document id.</param>
    /// <returns>The document key.</returns>
    /// <exception cref="ArgumentException">The document id is null, empty or not valid UTF-16.</exception>
    public static string Document(string partitionKey, string documentId)
    {
        if (string.IsNullOrEmpty(documentId)) throw new ArgumentException("Document id must not be null or empty.", nameof(documentId));
        RequireWellFormed(documentId, nameof(documentId));
        var id = Uri.EscapeDataString(documentId);
        return string.IsNullOrEmpty(partitionKey) ? id : partitionKey + "|" + id;
    }

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

    private static void RequireWellFormed(string s, string paramName)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i]))
                throw new ArgumentException("Value contains invalid UTF-16 (a lone surrogate).", paramName);
        }
    }
}
