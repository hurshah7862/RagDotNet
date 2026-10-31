using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagDotNet.Access;
using RagDotNet.Chunking;
using RagDotNet.Content;
using RagDotNet.Documents;
using RagDotNet.Pipeline;
using RagDotNet.Readers;
using RagDotNet.Storage;

namespace RagDotNet.Configuration;

/// <summary>Configures and builds an <see cref="IRagPipeline"/>.</summary>
public sealed class RagPipelineBuilder
{
    private readonly RagOptions _options = new();
    private readonly List<IDocumentReader> _readers = [];
    private IChatClient? _chat;
    private IEmbeddingGenerator<string, Embedding<float>>? _embedder;
    private VectorStore? _store;
    private ILoggerFactory? _loggerFactory;
    private IAccessScope? _scope;
    private RagEngine? _engine;
    private IChunkContentStore? _contentStore;

    /// <summary>Sets the chat client used to generate answers.</summary>
    /// <param name="client">The chat client.</param>
    /// <returns>This builder.</returns>
    public RagPipelineBuilder UseChatClient(IChatClient client)
    {
        _chat = client ?? throw new ArgumentNullException(nameof(client));
        return this;
    }

    /// <summary>Sets the embedding generator.</summary>
    /// <param name="generator">The generator.</param>
    /// <returns>This builder.</returns>
    public RagPipelineBuilder UseEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        _embedder = generator ?? throw new ArgumentNullException(nameof(generator));
        return this;
    }

    /// <summary>Sets the vector store, collection name and embedding dimensions.</summary>
    /// <param name="store">The vector store.</param>
    /// <param name="collectionName">Collection name for chunks.</param>
    /// <param name="dimensions">Embedding dimensions.</param>
    /// <returns>This builder.</returns>
    public RagPipelineBuilder UseVectorStore(VectorStore store, string collectionName, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        _store = store;
        _options.CollectionName = collectionName;
        _options.EmbeddingDimensions = dimensions;
        return this;
    }

    /// <summary>Adjusts the pipeline options.</summary>
    /// <param name="configure">Callback that edits the options.</param>
    /// <returns>This builder.</returns>
    public RagPipelineBuilder Configure(Action<RagOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options);
        return this;
    }

    /// <summary>Registers a document reader. App readers take precedence over the built-in text and Markdown readers.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>This builder.</returns>
    public RagPipelineBuilder AddReader(IDocumentReader reader)
    {
        _readers.Add(reader ?? throw new ArgumentNullException(nameof(reader)));
        return this;
    }

    /// <summary>Sets the logger factory.</summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <returns>This builder.</returns>
    public RagPipelineBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    /// <summary>Enforces an access scope on every ingest, retrieval and delete.</summary>
    /// <param name="scope">The scope provider.</param>
    /// <returns>This builder.</returns>
    public RagPipelineBuilder UseAccessScope(IAccessScope scope)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        return this;
    }

    /// <summary>Stores chunk text in <paramref name="store"/> instead of the vector store; sets <see cref="RagOptions.ContentStorage"/> to External.</summary>
    /// <param name="store">The content store.</param>
    /// <returns>This builder.</returns>
    public RagPipelineBuilder UseContentStore(IChunkContentStore store)
    {
        _contentStore = store ?? throw new ArgumentNullException(nameof(store));
        _options.ContentStorage = ContentStorageMode.External;
        return this;
    }

    /// <summary>Builds the pipeline. Repeated calls share one engine.</summary>
    /// <returns>The pipeline.</returns>
    /// <exception cref="InvalidOperationException">The embedding generator or vector store is missing.</exception>
    /// <exception cref="OptionsValidationException">The options are invalid, partition keys are configured without an access scope, or External content storage is selected without a content store.</exception>
    public IRagPipeline Build()
    {
        if (_embedder is null) throw new InvalidOperationException("No embedding generator configured. Call UseEmbeddingGenerator().");
        if (_store is null) throw new InvalidOperationException("No vector store configured. Call UseVectorStore().");
        Validate(requireScopeForPartitions: true);
        _engine ??= CreateEngine(_embedder, _store);
        return new RagPipeline(_engine, _scope, _chat);
    }

    /// <summary>Builds the admin interface over the same engine as <see cref="Build"/>. Admin operations ignore any access scope.</summary>
    /// <returns>The admin interface.</returns>
    /// <exception cref="InvalidOperationException">The embedding generator or vector store is missing.</exception>
    /// <exception cref="OptionsValidationException">The options are invalid or External content storage is selected without a content store.</exception>
    public IRagAdmin BuildAdmin()
    {
        if (_embedder is null) throw new InvalidOperationException("No embedding generator configured. Call UseEmbeddingGenerator().");
        if (_store is null) throw new InvalidOperationException("No vector store configured. Call UseVectorStore().");
        Validate(requireScopeForPartitions: false);
        _engine ??= CreateEngine(_embedder, _store);
        return new RagAdmin(_engine);
    }

    internal IChatClient? ChatClient => _chat;

    private RagEngine CreateEngine(IEmbeddingGenerator<string, Embedding<float>> embedder, VectorStore store) =>
        RagEngineFactory.Create(_options, _readers, embedder, store, _loggerFactory, _contentStore);

    private void Validate(bool requireScopeForPartitions)
    {
        var result = RagOptionsValidator.Validate(_options, _scope is not null, _contentStore is not null, requireScopeForPartitions);
        if (result.Failed) throw new OptionsValidationException(Options.DefaultName, typeof(RagOptions), result.Failures);
    }
}
