namespace RagDotNet.Retrieval;

/// <summary>
/// Hybrid search was required (<see cref="HybridMode.On"/>) but the vector store has no native hybrid search and no
/// keyword index is registered.
/// </summary>
public sealed class HybridSearchUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why hybrid search is unavailable and how to enable it.</param>
    public HybridSearchUnavailableException(string message)
        : base(message)
    {
    }
}
