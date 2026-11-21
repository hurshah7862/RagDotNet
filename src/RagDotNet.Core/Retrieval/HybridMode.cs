namespace RagDotNet.Retrieval;

/// <summary>Whether retrieval combines vector search with keyword search.</summary>
public enum HybridMode
{
    /// <summary>Hybrid when the vector store has native hybrid search or a keyword index is registered; otherwise vector-only, with a one-time warning.</summary>
    Auto,

    /// <summary>Always hybrid; retrieval throws <see cref="HybridSearchUnavailableException"/> when no keyword search is available.</summary>
    On,

    /// <summary>Vector-only. The keyword index is neither written nor built.</summary>
    Off,
}
