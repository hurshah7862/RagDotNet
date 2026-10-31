namespace RagDotNet.Documents;

/// <summary>Thrown when no registered reader can read a document.</summary>
public sealed class UnsupportedDocumentException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="fileName">File name of the unsupported document.</param>
    /// <param name="contentType">Content type, if any.</param>
    public UnsupportedDocumentException(string fileName, string? contentType)
        : base($"No document reader supports file '{fileName}' (content type: {contentType ?? "none"}).")
    {
        FileName = fileName;
        ContentType = contentType;
    }

    /// <summary>File name of the unsupported document.</summary>
    public string FileName { get; }

    /// <summary>Content type, if any.</summary>
    public string? ContentType { get; }
}
