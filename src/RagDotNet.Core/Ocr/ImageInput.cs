namespace RagDotNet.Ocr;

/// <summary>An image handed to an <see cref="IOcrProvider"/>: a rendered scanned page or an image embedded in a document.</summary>
/// <param name="Bytes">The encoded image, such as PNG or JPEG.</param>
/// <param name="MediaType">The image's media type, such as <c>image/png</c>.</param>
/// <param name="Width">Width in pixels, when known.</param>
/// <param name="Height">Height in pixels, when known.</param>
public sealed record ImageInput(ReadOnlyMemory<byte> Bytes, string MediaType, int? Width = null, int? Height = null);
