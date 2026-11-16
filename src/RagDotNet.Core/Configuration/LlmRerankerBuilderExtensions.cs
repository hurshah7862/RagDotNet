using RagDotNet.Retrieval;

namespace RagDotNet.Configuration;

/// <summary>Adds an <see cref="LlmReranker"/> to a <see cref="RagPipelineBuilder"/>.</summary>
public static class LlmRerankerBuilderExtensions
{
    /// <summary>
    /// Reranks retrieval candidates with <see cref="LlmReranker"/> over the builder's chat client, replacing an earlier reranker.
    /// </summary>
    /// <param name="builder">The pipeline builder.</param>
    /// <param name="configure">Optional options callback.</param>
    /// <returns>The same builder.</returns>
    /// <remarks><see cref="RagPipelineBuilder.Build"/> throws <see cref="InvalidOperationException"/> when no chat client is set.</remarks>
    public static RagPipelineBuilder AddLlmReranker(this RagPipelineBuilder builder, Action<LlmRerankerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new LlmRerankerOptions();
        configure?.Invoke(options);
        return builder.UseRerankerFactory((chat, tokenizer) => new LlmReranker(chat, options, tokenizer));
    }
}
