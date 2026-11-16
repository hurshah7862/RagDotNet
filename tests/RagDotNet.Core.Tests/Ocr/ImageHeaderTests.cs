using RagDotNet.Ocr;
using RagDotNet.TestAssetGen;

namespace RagDotNet.Core.Tests.Ocr;

public class ImageHeaderTests
{
    [Fact]
    public void Png_size_comes_from_the_IHDR_chunk()
    {
        Assert.True(ImageHeader.TryGetSize(Png.Solid(123, 45, 0x80), out var w, out var h));
        Assert.Equal((123, 45), (w, h));
    }

    [Fact]
    public void Jpeg_size_comes_from_the_SOF_segment_after_other_segments()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8, // SOI
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, // APP0, length 4
            0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00, // DHT, not a frame header
            0xFF, 0xC2, 0x00, 0x0B, 0x08, 0x01, 0x2C, 0x02, 0x58, 0x01, 0x01, 0x11, 0x00, // SOF2: 300 high, 600 wide
        ];

        Assert.True(ImageHeader.TryGetSize(jpeg, out var w, out var h));
        Assert.Equal((600, 300), (w, h));
    }

    [Fact]
    public void Gif_size_comes_from_the_logical_screen_descriptor()
    {
        byte[] gif = [.. "GIF89a"u8, 0x40, 0x01, 0x20, 0x00, 0, 0, 0];

        Assert.True(ImageHeader.TryGetSize(gif, out var w, out var h));
        Assert.Equal((320, 32), (w, h));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 })]
    public void Unknown_or_truncated_bytes_give_no_size(byte[] bytes) =>
        Assert.False(ImageHeader.TryGetSize(bytes, out _, out _));
}
