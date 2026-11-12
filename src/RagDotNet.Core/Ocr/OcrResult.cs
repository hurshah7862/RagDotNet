namespace RagDotNet.Ocr;

/// <summary>Text recognised in one image.</summary>
/// <param name="Text">The recognised text; empty when the image holds none.</param>
/// <param name="Confidence">How sure the engine is, from 0 to 1.</param>
public sealed record OcrResult(string Text, double Confidence);
