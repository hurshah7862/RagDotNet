namespace RagDotNet.Documents;

/// <summary>Reads a document format into structural blocks.</summary>
public interface IDocumentReader
{
    /// <summary>Whether this reader handles the given content type or file name.</summary>
    /// <param name="contentType">MIME content type, or null.</param>
    /// <param name="fileName">File name.</param>
    bool CanRead(string? contentType, string fileName);

    /// <summary>Reads the document.</summary>
    /// <param name="stream">Content stream.</param>
    /// <param name="context">Document context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken);
}
