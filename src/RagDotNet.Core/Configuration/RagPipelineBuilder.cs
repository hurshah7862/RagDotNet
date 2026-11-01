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

/// <summary>
/// Configures and builds an <see cref="IRagPipeline"/>. The builder is frozen by the first successful
/// <see cref="Build"/> or <see cref="BuildAdmin"/>: later configuration calls throw, and repeated builds share one engine.
/// </summary>
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
    private TimeProvider? _time;
    private bool _frozen;

    /// <summary>Sets the chat client used to generate answers.</summary>
    /// <param name="client">The chat client.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder has already built a pipeline or admin interface.</exception>
    public RagPipelineBuilder UseChatClient(IChatClient client)
    {
        ThrowIfFrozen();
        _chat = client ?? throw new ArgumentNullException(nameof(client));
        return this;
    }

    /// <summary>Sets the embedding generator.</summary>
    /// <param name="generator">The generator.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder has already built a pipeline or admin interface.</exception>
    public RagPipelineBuilder UseEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        ThrowIfFrozen();
        _embedder = generator ?? throw new ArgumentNullException(nameof(generator));
        return this;
    }

    /// <summary>Sets the vector store, collection name and embedding dimensions.</summary>
    /// <param name="store">The vector store.</param>
    /// <param name="collectionName">Collection name for chunks.</param>
    /// <param name="dimensions">Embedding dimensions.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder has already built a pipeline or admin interface.</exception>
    public RagPipelineBuilder UseVectorStore(VectorStore store, string collectionName, int dimensions)
    {
        ThrowIfFrozen();
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
    /// <exception cref="InvalidOperationException">The builder has already built a pipeline or admin interface.</exception>
    public RagPipelineBuilder Configure(Action<RagOptions> configure)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options);
        return this;
    }

    /// <summary>Registers a document reader. App readers take precedence over the built-in text and Markdown readers.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder has already built a pipeline or admin interface.</exception>
    public RagPipelineBuilder AddReader(IDocumentReader reader)
    {
        ThrowIfFrozen();
        _readers.Add(reader ?? throw new ArgumentNullException(nameof(reader)));
        return this;
    }

    /// <summary>Sets the logger factory.</summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder has already built a pipeline or admin interface.</exception>
    public RagPipelineBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ThrowIfFrozen();
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    /// <summary>Enforces an access scope on every ingest, retrieval and delete.</summary>
    /// <param name="scope">The scope provider.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder has already built a pipeline or admin interface.</exception>
    public RagPipelineBuilder UseAccessScope(IAccessScope scope)
    {
        ThrowIfFrozen();
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        return this;
    }

    /// <summary>Stores chunk text in <paramref name="store"/> instead of the vector store; sets <see cref="RagOptions.ContentStorage"/> to External.</summary>
    /// <param name="store">The content store.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder has already built a pipeline or admin interface.</exception>
    public RagPipelineBuilder UseContentStore(IChunkContentStore store)
    {
        ThrowIfFrozen();
        _contentStore = store ?? throw new ArgumentNullException(nameof(store));
        _options.ContentStorage = ContentStorageMode.External;
        return this;
    }

    /// <summary>Builds the pipeline and freezes the builder. Repeated calls share one engine.</summary>
    /// <returns>The pipeline.</returns>
    /// <exception cref="InvalidOperationException">The embedding generator or vector store is missing.</exception>
    /// <exception cref="OptionsValidationException">The options are invalid or External content storage is selected without a content store.</exception>
    public IRagPipeline Build()
    {
        if (_embedder is null) throw new InvalidOperationException("No embedding generator configured. Call UseEmbeddingGenerator().");
        if (_store is null) throw new InvalidOperationException("No vector store configured. Call UseVectorStore().");
        Validate();
        _engine ??= CreateEngine(_embedder, _store);
        _frozen = true;
        return new RagPipeline(_engine, _scope, _chat);
    }

    /// <summary>Builds the admin interface over the same engine as <see cref="Build"/> and freezes the builder. Admin operations ignore any access scope.</summary>
    /// <returns>The admin interface.</returns>
    /// <exception cref="InvalidOperationException">The embedding generator or vector store is missing.</exception>
    /// <exception cref="OptionsValidationException">The options are invalid or External content storage is selected without a content store.</exception>
    public IRagAdmin BuildAdmin()
    {
        if (_embedder is null) throw new InvalidOperationException("No embedding generator configured. Call UseEmbeddingGenerator().");
        if (_store is null) throw new InvalidOperationException("No vector store configured. Call UseVectorStore().");
        Validate();
        _engine ??= CreateEngine(_embedder, _store);
        _frozen = true;
        return new RagAdmin(_engine, _chat);
    }

    internal IChatClient? ChatClient => _chat;

    private RagEngine CreateEngine(IEmbeddingGenerator<string, Embedding<float>> embedder, VectorStore store) =>
        RagEngineFactory.Create(_options, _readers, embedder, store, _loggerFactory, _contentStore, _time);

    /// <summary>Sets the clock used for model check expiry and reindex heartbeats; tests use a manual clock.</summary>
    internal RagPipelineBuilder UseTimeProvider(TimeProvider time)
    {
        ThrowIfFrozen();
        _time = time ?? throw new ArgumentNullException(nameof(time));
        return this;
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException("RagPipelineBuilder cannot be changed after Build(); create a new builder.");
    }

    private void Validate()
    {
        var result = RagOptionsValidator.Validate(_options, _contentStore is not null);
        if (result.Failed) throw new OptionsValidationException(Options.DefaultName, typeof(RagOptions), result.Failures);
    }
}
