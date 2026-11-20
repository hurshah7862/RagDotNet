using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagDotNet.Configuration;
using RagDotNet.Keyword;
using RagDotNet.Retrieval;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers keyword indexes and rerankers for hybrid search with dependency injection.</summary>
public static class RagHybridBuilderExtensions
{
    /// <summary>Registers the keyword index (singleton), replacing the default in-memory index or an earlier registration.</summary>
    /// <typeparam name="TIndex">The index type.</typeparam>
    /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
    /// <returns>The same builder.</returns>
    public static IRagBuilder AddKeywordIndex<TIndex>(this IRagBuilder builder) where TIndex : class, IKeywordIndex
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IKeywordIndex, TIndex>());
        return builder;
    }

    /// <summary>Registers <see cref="InMemoryKeywordIndex"/>, the default, replacing an earlier keyword index registration.</summary>
    /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
    /// <returns>The same builder.</returns>
    public static IRagBuilder AddInMemoryKeywordIndex(this IRagBuilder builder) => builder.AddKeywordIndex<InMemoryKeywordIndex>();

    /// <summary>Registers the reranker (singleton), replacing an earlier one.</summary>
    /// <typeparam name="TReranker">The reranker type.</typeparam>
    /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
    /// <returns>The same builder.</returns>
    public static IRagBuilder AddReranker<TReranker>(this IRagBuilder builder) where TReranker : class, IReranker
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IReranker, TReranker>());
        return builder;
    }

    /// <summary>
    /// Registers <see cref="LlmReranker"/> over the container's <see cref="IChatClient"/>, replacing an earlier reranker.
    /// The host fails to start when no <see cref="IChatClient"/> is registered or an option is invalid.
    /// </summary>
    /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
    /// <param name="configure">Optional options callback.</param>
    /// <returns>The same builder.</returns>
    public static IRagBuilder AddLlmReranker(this IRagBuilder builder, Action<LlmRerankerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Services.AddOptions<LlmRerankerOptions>();
        if (configure is not null) options.Configure(configure);
        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<LlmRerankerOptions>, LlmRerankerOptionsValidator>());
        builder.Services.Replace(ServiceDescriptor.Singleton<IReranker>(sp => new LlmReranker(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<IOptions<LlmRerankerOptions>>().Value,
            sp.GetRequiredService<IOptions<RagOptions>>().Value.Tokenizer,
            sp.GetService<ILoggerFactory>()?.CreateLogger<LlmReranker>())));
        return builder;
    }

    private sealed class LlmRerankerOptionsValidator(IServiceProviderIsService services) : IValidateOptions<LlmRerankerOptions>
    {
        public ValidateOptionsResult Validate(string? name, LlmRerankerOptions options)
        {
            var failures = new List<string>();
            if (!services.IsService(typeof(IChatClient))) failures.Add("AddLlmReranker needs an IChatClient registered in the container.");
            if (options.BatchSize < 1) failures.Add("LlmRerankerOptions.BatchSize must be at least 1.");
            if (options.MaxConcurrency < 1) failures.Add("LlmRerankerOptions.MaxConcurrency must be at least 1.");
            if (options.MaxCandidateTokens < 1) failures.Add("LlmRerankerOptions.MaxCandidateTokens must be at least 1.");
            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
