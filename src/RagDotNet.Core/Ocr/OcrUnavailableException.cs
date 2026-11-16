namespace RagDotNet.Ocr;

/// <summary>
/// An OCR provider cannot work at all, for example because its engine or language data is not installed. Unlike a failure
/// on one image, which becomes a warning, this stops the ingest; the message says what to install.
/// </summary>
public sealed class OcrUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is missing and how to install it.</param>
    /// <param name="innerException">The underlying error, if any.</param>
    public OcrUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
