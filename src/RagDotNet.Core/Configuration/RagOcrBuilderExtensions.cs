using Microsoft.Extensions.DependencyInjection.Extensions;
using RagDotNet.Configuration;
using RagDotNet.Ocr;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers OCR providers and page renderers with dependency injection.</summary>
public static class RagOcrBuilderExtensions
{
    /// <summary>Registers the per-image OCR provider (singleton). A later registration replaces an earlier one.</summary>
    /// <typeparam name="TProvider">The provider type.</typeparam>
    /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
    /// <returns>The same builder.</returns>
    public static IRagBuilder AddOcrProvider<TProvider>(this IRagBuilder builder) where TProvider : class, IOcrProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IOcrProvider, TProvider>());
        return builder;
    }

    /// <summary>Registers the whole-document OCR provider (singleton). A later registration replaces an earlier one.</summary>
    /// <typeparam name="TProvider">The provider type.</typeparam>
    /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
    /// <returns>The same builder.</returns>
    public static IRagBuilder AddDocumentOcrProvider<TProvider>(this IRagBuilder builder) where TProvider : class, IDocumentOcrProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentOcrProvider, TProvider>());
        return builder;
    }

    /// <summary>Registers a page renderer (singleton), so scanned pages can be handed to the per-image OCR provider.</summary>
    /// <typeparam name="TRenderer">The renderer type.</typeparam>
    /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
    /// <returns>The same builder.</returns>
    public static IRagBuilder AddPageRenderer<TRenderer>(this IRagBuilder builder) where TRenderer : class, IPageRenderer
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IPageRenderer, TRenderer>());
        return builder;
    }
}
