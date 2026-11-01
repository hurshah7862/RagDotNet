using Microsoft.Extensions.DependencyInjection;
using RagDotNet.Access;
using RagDotNet.Chunking;
using RagDotNet.Content;
using RagDotNet.Documents;

namespace RagDotNet.Configuration;

/// <summary>Configures RagDotNet services registered with <c>AddRag</c>.</summary>
public interface IRagBuilder
{
    /// <summary>Gets the service collection being configured.</summary>
    IServiceCollection Services { get; }

    /// <summary>Registers a document reader. App readers take precedence over the built-in text and Markdown readers.</summary>
    /// <typeparam name="TReader">The reader type.</typeparam>
    /// <returns>This builder.</returns>
    IRagBuilder AddReader<TReader>() where TReader : class, IDocumentReader;

    /// <summary>Registers a custom chunker (singleton), selected by its <see cref="IChunker.Name"/> in <see cref="ChunkingOptions.Chunker"/>.</summary>
    /// <typeparam name="TChunker">The chunker type.</typeparam>
    /// <returns>This builder.</returns>
    IRagBuilder AddChunker<TChunker>() where TChunker : class, IChunker;

    /// <summary>Registers the per-request access scope (scoped lifetime).</summary>
    /// <typeparam name="TScope">The scope provider type.</typeparam>
    /// <returns>This builder.</returns>
    IRagBuilder AddAccessScope<TScope>() where TScope : class, IAccessScope;

    /// <summary>Stores chunk text in an external store (singleton) and sets <see cref="RagOptions.ContentStorage"/> to External.</summary>
    /// <typeparam name="TStore">The content store type.</typeparam>
    /// <returns>This builder.</returns>
    IRagBuilder UseExternalContent<TStore>() where TStore : class, IChunkContentStore;
}
