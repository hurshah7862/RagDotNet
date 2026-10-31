namespace RagDotNet.Chunking;

/// <summary>How a document is split into chunks.</summary>
public enum ChunkingStrategy
{
    /// <summary>Split along headings, paragraphs, sentences and lines, never across headings.</summary>
    StructureAware,
}
