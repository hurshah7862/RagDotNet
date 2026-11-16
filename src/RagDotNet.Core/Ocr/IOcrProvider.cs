namespace RagDotNet.Ocr;

/// <summary>
/// Recognises text in one image at a time. Ingest hands it scanned PDF pages (rendered by an <see cref="IPageRenderer"/>)
/// and images embedded in documents, and places the text where the image was.
/// </summary>
public interface IOcrProvider
{
    /// <summary>
    /// Names the engine and the settings that change its output, such as <c>tesseract/eng</c>. A change of id makes
    /// documents with scanned pages or images be read again on their next ingest.
    /// </summary>
    string Id { get; }

    /// <summary>Recognises the text in <paramref name="image"/>.</summary>
    /// <param name="image">The image.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text and the engine's confidence.</returns>
    /// <exception cref="OcrUnavailableException">The provider cannot work at all, for example its engine is not installed.</exception>
    Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken);
}
