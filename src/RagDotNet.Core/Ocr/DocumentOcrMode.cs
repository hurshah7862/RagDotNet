namespace RagDotNet.Ocr;

/// <summary>How a registered <see cref="IDocumentOcrProvider"/> is used.</summary>
public enum DocumentOcrMode
{
    /// <summary>The built-in reader reads the document; the provider's output is used only for pages with no text layer.</summary>
    FillMissingPages,

    /// <summary>For documents the provider can process, its output replaces the built-in reader's.</summary>
    ReplaceReader,
}
