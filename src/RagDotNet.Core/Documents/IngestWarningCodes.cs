namespace RagDotNet.Documents;

/// <summary>The <see cref="IngestWarning.Code"/> values the library and its readers use.</summary>
public static class IngestWarningCodes
{
    /// <summary>The document produced no text to index.</summary>
    public const string EmptyDocument = "EmptyDocument";

    /// <summary>A page has no extractable text, such as a scanned page; <see cref="IngestWarning.PageNumber"/> names it.</summary>
    public const string PageWithoutText = "PageWithoutText";

    /// <summary>Embedded images were not read, because reading them needs an OCR provider.</summary>
    public const string ImageNotRead = "ImageNotRead";

    /// <summary>OCR failed on a page or image, or the whole-document OCR provider failed; the rest of the document was read.</summary>
    public const string OcrFailed = "OcrFailed";

    /// <summary>OCR text was recognised with low confidence; it was kept.</summary>
    public const string OcrLowConfidence = "OcrLowConfidence";
}
