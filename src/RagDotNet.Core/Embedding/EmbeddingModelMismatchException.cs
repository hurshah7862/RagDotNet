namespace RagDotNet.Embedding;

/// <summary>Thrown when stored data was embedded with a different model than the one configured.</summary>
public sealed class EmbeddingModelMismatchException(string storedModelId, string configuredModelId)
    : Exception($"The store contains data embedded with model '{storedModelId}', but the configured embedding model is '{configuredModelId}'. Mixing models would corrupt retrieval; call IRagAdmin.ReindexAsync() to re-embed the stored data.")
{
    /// <summary>Gets the embedding model id found in the store.</summary>
    public string StoredModelId { get; } = storedModelId;

    /// <summary>Gets the embedding model id that is configured.</summary>
    public string ConfiguredModelId { get; } = configuredModelId;
}
