using Microsoft.Extensions.DependencyInjection.Extensions;
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
}
