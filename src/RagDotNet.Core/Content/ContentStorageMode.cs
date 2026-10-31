namespace RagDotNet.Content;

/// <summary>Where chunk text is stored.</summary>
public enum ContentStorageMode
{
    /// <summary>Chunk text is stored in the vector store record.</summary>
    VectorStore,

    /// <summary>Chunk text is stored externally; the vector store holds no text.</summary>
    External,
}
