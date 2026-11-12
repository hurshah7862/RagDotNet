namespace RagDotNet.Ocr;

/// <summary>How ingest uses a registered OCR provider. These options have no effect when no provider is registered.</summary>
public sealed class OcrOptions
{
    /// <summary>Whether images embedded in documents are read. Scanned pages are always read. Default true.</summary>
    public bool OcrEmbeddedImages { get; set; } = true;

    /// <summary>Images narrower or shorter than this many pixels, such as icons, are skipped. Default 64.</summary>
    public int MinImageSize { get; set; } = 64;

    /// <summary>
    /// Text recognised with a confidence below this (0 to 1) is kept, with an <c>OcrLowConfidence</c> warning. Default 0.6.
    /// </summary>
    public double MinConfidence { get; set; } = 0.6;

    /// <summary>Resolution scanned pages are rendered at, from 72 to 1200. Default 300.</summary>
    public int PageDpi { get; set; } = 300;

    /// <summary>How a whole-document provider is used. Default <see cref="DocumentOcrMode.FillMissingPages"/>.</summary>
    public DocumentOcrMode DocumentProviderMode { get; set; } = DocumentOcrMode.FillMissingPages;
}
