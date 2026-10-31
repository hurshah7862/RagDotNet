namespace RagDotNet.Documents;

/// <summary>The structural kind of a parsed document block.</summary>
public enum BlockKind
{
    /// <summary>A heading.</summary>
    Heading,
    /// <summary>A paragraph of prose.</summary>
    Paragraph,
    /// <summary>A bulleted or numbered list.</summary>
    List,
    /// <summary>A table.</summary>
    Table,
    /// <summary>A code block.</summary>
    Code,
}
