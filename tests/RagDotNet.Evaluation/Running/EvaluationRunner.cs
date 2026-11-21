using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Documents;
using RagDotNet.Evaluation.Datasets;
using RagDotNet.Evaluation.Scoring;
using RagDotNet.Ingestion;
using RagDotNet.Retrieval;

namespace RagDotNet.Evaluation.Running;

/// <summary>Runs every question of a dataset through each configuration using RagDotNet's public API.</summary>
/// <param name="embedder">The embedding generator.</param>
/// <param name="dimensions">Its dimensions.</param>
/// <param name="chat">The chat client for multi-query and hyde, or null.</param>
/// <param name="ocr">OCR for the scanned check, or null.</param>
/// <param name="reranker">The reranker for <c>hybrid-rerank</c>, or null.</param>
public sealed class EvaluationRunner(
    IEmbeddingGenerator<string, Embedding<float>> embedder, int dimensions, IChatClient? chat, OcrSetup? ocr = null, IReranker? reranker = null)
{
    /// <summary>Chunks retrieved per question.</summary>
    public const int TopK = 10;

    /// <summary>
    /// Scores each configuration on each set, set by set. Each set is ingested into its own stores, so its questions only
    /// see its own documents. Within a set, configurations sharing chunker, MaxTokens and overlap share one ingestion.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A mode other than <c>vector</c> is requested without a chat client, or <c>hybrid-rerank</c> without a reranker.
    /// </exception>
    public async Task<IReadOnlyList<ConfigResult>> RunAsync(IReadOnlyList<DatasetSet> sets, IReadOnlyList<EvaluationConfig> configs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(configs);
        if (chat is null && configs.FirstOrDefault(c => c.Mode != EvaluationConfig.Vector) is { } needsChat)
        {
            throw new InvalidOperationException($"Mode '{needsChat.Mode}' needs a chat client.");
        }

        if (reranker is null && configs.Any(c => c.Retrieval == EvaluationConfig.RetrievalHybridRerank))
        {
            throw new InvalidOperationException("Retrieval 'hybrid-rerank' needs a reranker.");
        }

        var all = new List<ConfigResult>();
        foreach (var set in sets)
        {
            // Only the scanned check reads with OCR, so the other formats score exactly as they did before OCR existed.
            all.AddRange(await RunSetAsync(set.Label, set.Dataset, set.UnscorableIds ?? [], configs, set.Format == DatasetSet.Scanned ? ocr : null, ct).ConfigureAwait(false));
        }

        return all;
    }

    private async Task<IReadOnlyList<ConfigResult>> RunSetAsync(
        string setName, Dataset dataset, IReadOnlyList<string> unscorable, IReadOnlyList<EvaluationConfig> configs, OcrSetup? setOcr, CancellationToken ct)
    {
        var results = new List<ConfigResult>(configs.Count);
        foreach (var group in configs.GroupBy(c => (c.Chunker, c.MaxTokens, c.Overlap)))
        {
            var builder = new RagPipelineBuilder()
                .UseEmbeddingGenerator(embedder)
                .UseVectorStore(new InMemoryVectorStore(), "eval", dimensions)
                .AddPdfReader()
                .AddDocxReader()
                .AddHtmlReader()
                .Configure(o =>
                {
                    o.Chunking = new ChunkingOptions
                    {
                        Chunker = group.Key.Chunker,
                        MaxTokens = group.Key.MaxTokens,
                        OverlapRatio = group.Key.Overlap,
                    };
                    // The keyword index is built only when a hybrid retrieval will read it; vector retrieval turns hybrid off per call.
                    o.Hybrid = group.Any(c => c.Retrieval != EvaluationConfig.RetrievalVector) ? HybridMode.Auto : HybridMode.Off;
                });
            if (chat is not null)
            {
                builder.UseChatClient(chat);
            }

            if (reranker is not null)
            {
                builder.UseReranker(reranker);
            }

            if (setOcr is not null)
            {
                builder.UseOcrProvider(setOcr.Provider).AddPageRenderer(setOcr.Renderer);
            }

            var pipeline = builder.Build();
            var ingested = new List<IngestResult>();
            foreach (var doc in dataset.Documents)
            {
                ingested.Add(await pipeline.IngestAsync(DocumentSource.FromFile(doc.FilePath, doc.DocumentId), cancellationToken: ct).ConfigureAwait(false));
            }

            var chunkCount = ingested.Sum(r => r.ChunkCount);
            foreach (var config in group)
            {
                var options = new RetrievalOptions
                {
                    TopK = TopK,
                    QueryTransformers = Transformers(config.Mode),
                    Hybrid = config.Retrieval == EvaluationConfig.RetrievalVector ? HybridMode.Off : HybridMode.On,
                    UseReranker = config.Retrieval == EvaluationConfig.RetrievalHybridRerank,
                };
                var ranks = new List<int?>(dataset.Questions.Count);
                foreach (var q in dataset.Questions)
                {
                    var retrieved = await pipeline.RetrieveAsync(q.Text, options, ct).ConfigureAwait(false);
                    ranks.Add(unscorable.Contains(q.Id) ? null : Metrics.RankOfFirstRelevant(retrieved, q));
                }

                results.Add(new ConfigResult(setName, config, chunkCount, Metrics.Summarize(ranks), ranks));
            }
        }

        // GroupBy keeps first-seen group order, but configs of one group may be interleaved with others; restore input order.
        return [.. configs.Select(c => results.First(r => r.Config == c))];
    }

    private static IReadOnlyList<IQueryTransformer> Transformers(string mode) => mode switch
    {
        EvaluationConfig.Vector => [],
        EvaluationConfig.MultiQuery => [new MultiQueryTransformer(3)],
        EvaluationConfig.Hyde => [new HydeQueryTransformer()],
        _ => throw new ArgumentException($"Unknown mode '{mode}'.", nameof(mode)),
    };
}
