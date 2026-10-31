using System.Text;

namespace RagDotNet.Readers;

internal static class TextDecoding
{
    // throwOnInvalidBytes: false => invalid bytes become U+FFFD.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Decodes a stream honoring UTF-8/UTF-16 BOMs; invalid bytes become U+FFFD.</summary>
    public static string Decode(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Utf8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        return Utf8.GetString(bytes);
    }
}
