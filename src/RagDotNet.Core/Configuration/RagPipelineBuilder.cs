using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;
using RagDotNet.Chunking;
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
    private RagEngine? _engine;

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

    /// <summary>Builds the pipeline. Repeated calls share one engine.</summary>
    /// <returns>The pipeline.</returns>
    /// <exception cref="InvalidOperationException">The embedding generator or vector store is missing.</exception>
    /// <exception cref="NotSupportedException">External content storage is selected; it throws until a content store is configured.</exception>
    public IRagPipeline Build()
    {
        if (_embedder is null) throw new InvalidOperationException("No embedding generator configured. Call UseEmbeddingGenerator().");
        if (_store is null) throw new InvalidOperationException("No vector store configured. Call UseVectorStore().");
        _engine ??= CreateEngine(_embedder, _store);
        return new RagPipeline(_engine);
    }

    internal IChatClient? ChatClient => _chat;

    private RagEngine CreateEngine(IEmbeddingGenerator<string, Embedding<float>> embedder, VectorStore store)
    {
        var tokenizer = _options.Tokenizer ?? TokenCounter.CreateDefault();
        var tokenizerName = _options.Tokenizer is null ? "cl100k_base" : _options.Tokenizer.GetType().FullName ?? "custom";
        var readers = new ReaderSelector(_readers.Concat<IDocumentReader>([new MarkdownReader(), new PlainTextReader()]));
        var logger = (_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("RagDotNet");
        return new RagEngine(
            _options, readers, new StructureAwareChunker(tokenizer), new TokenCounter(tokenizer), tokenizerName,
            embedder, new ChunkStore(store, _options), new ManifestStore(store, _options), logger);
    }
}
