using RagDotNet.Ocr;

namespace RagDotNet.Evaluation.Datasets;

/// <summary>The OCR provider and page renderer the scanned check reads with; the loader and the pipeline use the same ones.</summary>
/// <param name="Provider">The per-image OCR provider.</param>
/// <param name="Renderer">The PDF page renderer.</param>
public sealed record OcrSetup(IOcrProvider Provider, IPageRenderer Renderer)
{
    /// <summary>The same OCR stage ingest runs, with the library's default options.</summary>
    internal OcrStage Stage() => new(Provider, null, [Renderer], new OcrOptions());
}
