namespace RagDotNet.Chunking;

/// <summary>Names of the built-in chunkers, for <see cref="ChunkingOptions.Chunker"/>.</summary>
public static class Chunkers
{
    /// <summary>Splits along headings, paragraphs, sentences and lines, never across headings.</summary>
    public const string StructureAware = "structure-aware";
}
