namespace RagDotNet.Documents;

/// <summary>A non-fatal problem found while ingesting a document.</summary>
/// <param name="Code">Machine-readable warning code.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="PageNumber">Page number when relevant.</param>
public sealed record IngestWarning(string Code, string Message, int? PageNumber = null);
