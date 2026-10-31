using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;

namespace RagDotNet.Core.Tests.Fakes;

/// <summary>Vector store wrapper whose manifest collection can be told to fail its next upsert, simulating a crash.</summary>
public sealed class FailingVectorStore(VectorStore inner) : VectorStore
{
    public bool FailNextManifestUpsert { get; set; }

    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null) =>
        inner.GetCollection<TKey, TRecord>(name, definition);

    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition definition)
    {
        var c = inner.GetDynamicCollection(name, definition);
        return name.EndsWith("_documents", StringComparison.Ordinal) ? new FailingCollection(this, c) : c;
    }

    public override IAsyncEnumerable<string> ListCollectionNamesAsync(CancellationToken cancellationToken = default) =>
        inner.ListCollectionNamesAsync(cancellationToken);

    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) =>
        inner.CollectionExistsAsync(name, cancellationToken);

    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default) =>
        inner.EnsureCollectionDeletedAsync(name, cancellationToken);

    public override object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    private sealed class FailingCollection(FailingVectorStore owner, VectorStoreCollection<object, Dictionary<string, object?>> c)
        : VectorStoreCollection<object, Dictionary<string, object?>>
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

        public override Task UpsertAsync(Dictionary<string, object?> record, CancellationToken cancellationToken = default)
        {
            if (owner.FailNextManifestUpsert)
            {
                owner.FailNextManifestUpsert = false;
                throw new IOException("Simulated crash before the manifest was written.");
            }

            return c.UpsertAsync(record, cancellationToken);
        }

        public override Task UpsertAsync(IEnumerable<Dictionary<string, object?>> records, CancellationToken cancellationToken = default) =>
            c.UpsertAsync(records, cancellationToken);

        public override IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<Dictionary<string, object?>>? options = null, CancellationToken cancellationToken = default) =>
            c.SearchAsync(searchValue, top, options, cancellationToken);

        public override object? GetService(Type serviceType, object? serviceKey = null) => c.GetService(serviceType, serviceKey);
    }
}
