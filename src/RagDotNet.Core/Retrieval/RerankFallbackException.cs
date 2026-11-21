namespace RagDotNet.Retrieval;

/// <summary>
/// Thrown by a built-in reranker that gave up (for example, the model's reply could not be read twice). Retrieval then
/// returns the fused order and logs a warning instead of failing.
/// </summary>
internal sealed class RerankFallbackException(string message, Exception? innerException = null) : Exception(message, innerException);
