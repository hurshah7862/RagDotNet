using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;
using RagDotNet.Chunking;
using RagDotNet.Content;
using RagDotNet.Documents;
using RagDotNet.Ocr;
using RagDotNet.Pipeline;
using RagDotNet.Readers;
using RagDotNet.Storage;

namespace RagDotNet.Configuration;

/// <summary>The single construction path for <see cref="RagEngine"/>, shared by the builder and dependency injection.</summary>
internal static class RagEngineFactory
{
    public static RagEngine Create(
        RagOptions options,
        IEnumerable<IDocumentReader> appReaders,
        IEnumerable<IChunker> appChunkers,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        VectorStore store,
        ILoggerFactory? loggerFactory,
        IChunkContentStore? contentStore,
        TimeProvider? time = null,
        IOcrProvider? ocr = null,
        IDocumentOcrProvider? documentOcr = null,
        IEnumerable<IPageRenderer>? renderers = null,
        Keyword.IKeywordIndex? keywordIndex = null,
        Retrieval.IReranker? reranker = null)
    {
        if (options.Hybrid == Retrieval.HybridMode.On && options.ContentStorage == ContentStorageMode.External && keywordIndex is null)
            throw new ArgumentException(RagOptionsValidator.ExternalHybridNeedsIndex);
        var tokenizer = options.Tokenizer ?? TokenCounter.CreateDefault();
        var tokenizerName = options.Tokenizer is null ? "cl100k_base" : options.Tokenizer.GetType().FullName ?? "custom";
        var readers = new ReaderSelector(appReaders.Concat<IDocumentReader>([new MarkdownReader(), new PlainTextReader()]));
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("RagDotNet");
        return new RagEngine(
            options, readers, new ChunkerRegistry(appChunkers, [new StructureAwareChunker(tokenizer), new FixedSizeChunker(tokenizer), new SemanticChunker(tokenizer, embedder, options.EmbeddingBatchSize, Math.Max(0, options.EmbeddingDimensions))]), new TokenCounter(tokenizer), tokenizerName,
            embedder, new ChunkStore(store, options), new ManifestStore(store, options), logger, contentStore, time)
        {
            OcrProvider = ocr,
            DocumentOcrProvider = documentOcr,
            PageRenderers = renderers?.ToList() ?? [],
            KeywordIndex = keywordIndex,
            Reranker = reranker,
        };
    }
}
