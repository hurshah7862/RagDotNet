using RagDotNet.Documents;

namespace RagDotNet.Ocr;

/// <summary>
/// Reads a whole document at once, as services such as Azure Document Intelligence do. By default its output fills only
/// the pages that have no text layer; <see cref="DocumentOcrMode.ReplaceReader"/> uses it for the whole document.
/// </summary>
public interface IDocumentOcrProvider
{
    /// <summary>Names the service and the settings that change its output. A change makes affected documents be read again.</summary>
    string Id { get; }

    /// <summary>Whether this provider handles the given content type or file name.</summary>
    /// <param name="contentType">MIME content type, or null.</param>
    /// <param name="fileName">File name.</param>
    bool CanProcess(string? contentType, string fileName);

    /// <summary>Reads the document.</summary>
    /// <param name="document">The document's bytes.</param>
    /// <param name="context">The document's name and content type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Blocks with page numbers; a provider that only knows plain text returns one paragraph per page.</returns>
    Task<DocumentOcrResult> ProcessAsync(ReadOnlyMemory<byte> document, DocumentReadContext context, CancellationToken cancellationToken);
}
