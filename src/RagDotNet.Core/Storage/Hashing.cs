using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RagDotNet.Chunking;

namespace RagDotNet.Storage;

internal static class Hashing
{
    public static string ContentHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Hash of validated metadata, independent of key order.</summary>
    public static string MetadataHash(IReadOnlyDictionary<string, object?> metadata)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(Format(v)).Append('\n');
        return ContentHash(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string Format(object? v) => v switch
    {
        null => "null",
        string s => "s" + s.Length.ToString(CultureInfo.InvariantCulture) + ":" + s,
        bool b => b ? "b:true" : "b:false",
        int i => "n:" + i.ToString(CultureInfo.InvariantCulture),
        long l => "n:" + l.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset d => "d:" + d.ToString("O", CultureInfo.InvariantCulture),
        // Count, then each element length-prefixed, so [] differs from [""] and no two arrays share an encoding.
        string[] a => "a" + a.Length.ToString(CultureInfo.InvariantCulture) + ":" + string.Concat(a.Select(Format)),
        _ => throw new ArgumentException($"Unsupported metadata value type '{v.GetType()}'."),
    };

    public static string ChunkingHash(ChunkingOptions o, string tokenizerName)
    {
        var s = string.Create(CultureInfo.InvariantCulture,
            $"{o.Strategy}|{o.MaxTokens}|{o.OverlapRatio:R}|{tokenizerName}|embed:{EmbeddingText.FormatVersion}");
        return ContentHash(Encoding.UTF8.GetBytes(s));
    }
}
