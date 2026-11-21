using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.VectorData;

namespace RagDotNet.Core.Tests.Fakes;

/// <summary>
/// An in-memory vector store whose chunk collection also offers native hybrid search: vector search re-ordered by how
/// many of the keywords each record's Text contains.
/// </summary>
internal sealed class HybridVectorStore : VectorStore
{
    private readonly InMemoryVectorStore _inner = new();

    public int HybridCalls;

    public int VectorCalls;

    public ICollection<string>? LastKeywords { get; private set; }

    public bool LastHadFilter { get; private set; }

    public string? LastAdditionalProperty { get; private set; }

    /// <summary>Makes every hybrid call throw NotSupportedException, as a connector that cannot serve dynamic records would.</summary>
    public bool ThrowNotSupported { get; set; }

    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null) =>
        _inner.GetCollection<TKey, TRecord>(name, definition);

    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition definition)
    {
        var c = _inner.GetDynamicCollection(name, definition);
        return name.EndsWith("_documents", StringComparison.Ordinal) ? c : new HybridCollection(this, c);
    }

    public override IAsyncEnumerable<string> ListCollectionNamesAsync(CancellationToken cancellationToken = default) => _inner.ListCollectionNamesAsync(cancellationToken);

    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) => _inner.CollectionExistsAsync(name, cancellationToken);

    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default) => _inner.EnsureCollectionDeletedAsync(name, cancellationToken);

    public override object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    private sealed class HybridCollection(HybridVectorStore owner, VectorStoreCollection<object, Dictionary<string, object?>> c)
        : VectorStoreCollection<object, Dictionary<string, object?>>, IKeywordHybridSearchable<Dictionary<string, object?>>
    {
        public override string Name => c.Name;

        public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) => c.CollectionExistsAsync(cancellationToken);

        public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) => c.EnsureCollectionExistsAsync(cancellationToken);

        public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default) => c.EnsureCollectionDeletedAsync(cancellationToken);

        public override Task<Dictionary<string, object?>?> GetAsync(object key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default) =>
            c.GetAsync(key, options, cancellationToken);

        public override IAsyncEnumerable<Dictionary<string, object?>> GetAsync(IEnumerable<object> keys, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default) =>
            c.GetAsync(keys, options, cancellationToken);

        public override IAsyncEnumerable<Dictionary<string, object?>> GetAsync(Expression<Func<Dictionary<string, object?>, bool>> filter, int top, FilteredRecordRetrievalOptions<Dictionary<string, object?>>? options = null, CancellationToken cancellationToken = default) =>
            c.GetAsync(filter, top, options, cancellationToken);

        public override Task DeleteAsync(object key, CancellationToken cancellationToken = default) => c.DeleteAsync(key, cancellationToken);

        public override Task DeleteAsync(IEnumerable<object> keys, CancellationToken cancellationToken = default) => c.DeleteAsync(keys, cancellationToken);

        public override Task UpsertAsync(Dictionary<string, object?> record, CancellationToken cancellationToken = default) => c.UpsertAsync(record, cancellationToken);

        public override Task UpsertAsync(IEnumerable<Dictionary<string, object?>> records, CancellationToken cancellationToken = default) => c.UpsertAsync(records, cancellationToken);

        public override IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<Dictionary<string, object?>>? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.VectorCalls);
            return c.SearchAsync(searchValue, top, options, cancellationToken);
        }

        public async IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> HybridSearchAsync<TInput>(
            TInput searchValue, ICollection<string> keywords, int top, HybridSearchOptions<Dictionary<string, object?>>? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TInput : notnull
        {
            Interlocked.Increment(ref owner.HybridCalls);
            owner.LastKeywords = keywords;
            owner.LastHadFilter = options?.Filter is not null;
            owner.LastAdditionalProperty = options?.AdditionalProperty?.ToString();
            if (owner.ThrowNotSupported) throw new NotSupportedException("Hybrid search is not supported for dynamic records.");

            var all = new List<(Dictionary<string, object?> Record, double Score)>();
            await foreach (var hit in c.SearchAsync(searchValue, 1000, new VectorSearchOptions<Dictionary<string, object?>> { Filter = options?.Filter }, cancellationToken))
            {
                var text = (hit.Record.TryGetValue("Text", out var t) ? t as string : null) ?? "";
                var matches = keywords.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
                all.Add((hit.Record, matches + (hit.Score ?? 0) / 10));
            }

            foreach (var (record, score) in all.OrderByDescending(x => x.Score).Take(top))
                yield return new VectorSearchResult<Dictionary<string, object?>>(record, score);
        }

        public override object? GetService(Type serviceType, object? serviceKey = null) => c.GetService(serviceType, serviceKey);
    }
}

/// <summary>Hands one <see cref="FakeLogger"/> to every category.</summary>
internal sealed class CapturingLoggerFactory(FakeLogger logger) : ILoggerFactory
{
    public CapturingLoggerFactory()
        : this(new FakeLogger())
    {
    }

    public FakeLogger Logger => logger;

    public IReadOnlyList<string> Messages(LogLevel level) =>
        logger.Collector.GetSnapshot().Where(r => r.Level == level).Select(r => r.Message).ToList();

    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName) => logger;

    public void Dispose() { }
}
