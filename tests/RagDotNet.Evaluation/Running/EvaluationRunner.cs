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
public sealed class EvaluationRunner(IEmbeddingGenerator<string, Embedding<float>> embedder, int dimensions, IChatClient? chat)
{
    /// <summary>Chunks retrieved per question.</summary>
    public const int TopK = 10;

    /// <summary>
    /// Scores each configuration on each set, set by set. Each set is ingested into its own stores, so its questions only
    /// see its own documents. Within a set, configurations sharing chunker, MaxTokens and overlap share one ingestion.
    /// </summary>
    /// <exception cref="InvalidOperationException">A mode other than <c>vector</c> is requested without a chat client.</exception>
    public async Task<IReadOnlyList<ConfigResult>> RunAsync(IReadOnlyList<DatasetSet> sets, IReadOnlyList<EvaluationConfig> configs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(configs);
        if (chat is null && configs.FirstOrDefault(c => c.Mode != EvaluationConfig.Vector) is { } needsChat)
        {
            throw new InvalidOperationException($"Mode '{needsChat.Mode}' needs a chat client.");
        }

        var all = new List<ConfigResult>();
        foreach (var set in sets)
        {
            all.AddRange(await RunSetAsync(set.Label, set.Dataset, set.UnscorableIds ?? [], configs, ct).ConfigureAwait(false));
        }

        return all;
    }

    private async Task<IReadOnlyList<ConfigResult>> RunSetAsync(string setName, Dataset dataset, IReadOnlyList<string> unscorable, IReadOnlyList<EvaluationConfig> configs, CancellationToken ct)
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
                .Configure(o => o.Chunking = new ChunkingOptions
                {
                    Chunker = group.Key.Chunker,
                    MaxTokens = group.Key.MaxTokens,
                    OverlapRatio = group.Key.Overlap,
                });
            if (chat is not null)
            {
                builder.UseChatClient(chat);
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
                var options = new RetrievalOptions { TopK = TopK, QueryTransformers = Transformers(config.Mode) };
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
