namespace RagDotNet.Ocr.Tesseract;

/// <summary>Where Tesseract's language data is, and which languages to read.</summary>
public sealed class TesseractOcrOptions
{
    /// <summary>
    /// The folder holding the <c>.traineddata</c> files, which are not included in the package. Download them from
    /// https://github.com/tesseract-ocr/tessdata_fast. Default <c>./tessdata</c>.
    /// </summary>
    public string DataPath { get; set; } = "./tessdata";

    /// <summary>Languages in Tesseract's syntax, such as <c>eng</c> or <c>eng+deu</c>. Default <c>eng</c>.</summary>
    public string Languages { get; set; } = "eng";
}
