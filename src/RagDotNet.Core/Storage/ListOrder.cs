using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RagDotNet.Storage;

/// <summary>
/// The stable number the document list pages by. Some stores (Qdrant) cannot order by the record key, and every
/// supported store can order by an indexed long. It is the top 53 bits of the first 8 bytes of SHA-256 of the string
/// key (read little-endian), so it is the same in every process and on every platform, the same for String and Guid key
/// settings, and exact as a double: some stores (Qdrant) compare numeric payloads as doubles in range filters. Equal
/// values are rare and the listing handles them.
/// </summary>
internal static class ListOrder
{
    public static long Of(string manifestKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(manifestKey), hash);
        return (long)(BinaryPrimitives.ReadUInt64LittleEndian(hash) >> 11);
    }
}
