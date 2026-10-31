namespace RagDotNet.Documents;

/// <summary>A document to ingest: either an open stream or a file path, plus metadata.</summary>
public sealed class DocumentSource
{
    /// <summary>Creates a source over an already-open stream.</summary>
    /// <param name="documentId">Stable, non-blank identifier of the document.</param>
    /// <param name="stream">The content stream.</param>
    /// <param name="fileName">File name, used for format detection.</param>
    /// <param name="contentType">Optional MIME content type.</param>
    public DocumentSource(string documentId, Stream stream, string fileName, string? contentType = null)
    {
        ValidateId(documentId);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(fileName);
        DocumentId = documentId;
        Stream = stream;
        FileName = fileName;
        ContentType = contentType;
    }

    private DocumentSource(string documentId, string filePath)
    {
        ValidateId(documentId);
        DocumentId = documentId;
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
    }

    /// <summary>Creates a source that reads from a file on disk.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="documentId">Document id; defaults to the full path.</param>
    public static DocumentSource FromFile(string path, string? documentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new DocumentSource(documentId ?? Path.GetFullPath(path), path);
    }

    /// <summary>Stable identifier of the document.</summary>
    public string DocumentId { get; }

    /// <summary>The content stream, or null when the source is a file path.</summary>
    public Stream? Stream { get; }

    /// <summary>The file path, or null when the source is a stream.</summary>
    public string? FilePath { get; }

    /// <summary>File name used for format detection.</summary>
    public string FileName { get; }

    /// <summary>Optional MIME content type.</summary>
    public string? ContentType { get; }

    /// <summary>Optional title override.</summary>
    public string? Title { get; set; }

    /// <summary>Arbitrary metadata stored with the document.</summary>
    public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();

    private static void ValidateId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("Document id must not be empty or whitespace.", nameof(documentId));
        }
    }
}
