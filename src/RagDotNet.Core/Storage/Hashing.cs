using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RagDotNet.Chunking;

namespace RagDotNet.Storage;

internal static class Hashing
{
    public static string ContentHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string ChunkingHash(ChunkingOptions o, string tokenizerName)
    {
        var s = string.Create(CultureInfo.InvariantCulture,
            $"{o.Strategy}|{o.MaxTokens}|{o.OverlapRatio:R}|{tokenizerName}|embed:{EmbeddingText.FormatVersion}");
        return ContentHash(Encoding.UTF8.GetBytes(s));
    }
}
