namespace RagDotNet.Documents;

/// <summary>
/// Thrown by a document reader when a file of a supported format cannot be read, for example because it is corrupt,
/// truncated or password-protected.
/// </summary>
public sealed class DocumentReadException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="fileName">File name of the document that could not be read.</param>
    /// <param name="reason">Why it could not be read, in plain words.</param>
    /// <param name="inner">The parser's exception, if any.</param>
    public DocumentReadException(string fileName, string reason, Exception? inner = null)
        : base($"Could not read '{fileName}': {reason}", inner)
    {
        FileName = fileName;
        Reason = reason;
    }

    /// <summary>File name of the document that could not be read.</summary>
    public string FileName { get; }

    /// <summary>Why it could not be read.</summary>
    public string Reason { get; }
}
