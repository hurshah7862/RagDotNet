namespace RagDotNet.Documents;

/// <summary>A structural block of a parsed document.</summary>
/// <param name="Kind">Block kind.</param>
/// <param name="Text">Block text.</param>
/// <param name="HeadingPath">Enclosing headings; for a heading block this includes the heading itself.</param>
/// <param name="PageNumber">Page number when known.</param>
public sealed record DocumentBlock(BlockKind Kind, string Text, IReadOnlyList<string> HeadingPath, int? PageNumber = null);
