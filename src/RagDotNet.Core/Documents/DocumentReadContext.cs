namespace RagDotNet.Documents;

/// <summary>Information about the document being read.</summary>
/// <param name="FileName">File name.</param>
/// <param name="ContentType">MIME content type, if known.</param>
public sealed record DocumentReadContext(string FileName, string? ContentType);
