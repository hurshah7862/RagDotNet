using System.Buffers.Binary;

namespace RagDotNet.Ocr;

/// <summary>Reads an image's pixel size from its header, for PNG, JPEG and GIF, without decoding it.</summary>
internal static class ImageHeader
{
    public static bool TryGetSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            // PNG: the IHDR chunk always comes first.
            width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
            height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..]);
            return width > 0 && height > 0;
        }

        if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
            return width > 0 && height > 0;
        }

        return bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && TryJpeg(bytes, out width, out height);
    }

    private static bool TryJpeg(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        var i = 2;
        while (i + 4 <= bytes.Length)
        {
            if (bytes[i] != 0xFF) return false;
            var marker = bytes[i + 1];
            if (marker == 0xFF) { i++; continue; } // fill byte
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; } // markers without a length
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 2)..]);
            if (length < 2) return false;

            // Start-of-frame markers C0–CF, except DHT (C4), JPG (C8) and DAC (CC), hold the size.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 9 > bytes.Length) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 7)..]);
                return width > 0 && height > 0;
            }

            i += 2 + length;
        }

        return false;
    }
}
