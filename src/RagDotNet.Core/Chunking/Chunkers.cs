namespace RagDotNet.Chunking;

/// <summary>Names of the built-in chunkers, for <see cref="ChunkingOptions.Chunker"/>.</summary>
public static class Chunkers
{
    /// <summary>Splits along headings, paragraphs, sentences and lines, never across headings.</summary>
    public const string StructureAware = "structure-aware";

    /// <summary>A sliding token window over the document's body text.</summary>
    public const string FixedSize = "fixed-size";

    /// <summary>Breaks where the meaning of neighbouring sentences shifts. Makes embedding calls during ingest.</summary>
    public const string Semantic = "semantic";
}
