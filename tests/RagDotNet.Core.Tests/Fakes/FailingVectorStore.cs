using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;

namespace RagDotNet.Core.Tests.Fakes;

/// <summary>Vector store wrapper whose manifest collection can be told to fail its next upsert, simulating a crash.</summary>
public sealed class FailingVectorStore(VectorStore inner) : VectorStore
{
    /// <summary>1-based: the Nth upcoming manifest upsert fails (0 = none).</summary>
    public int FailManifestUpsertIn { get; set; }

    /// <summary>When set, chunk searches return results whose Score is null.</summary>
    public bool NullSearchScores { get; set; }

    /// <summary>When set, chunk searches return records with this column removed.</summary>
    public string? DropColumnOnSearch { get; set; }

    /// <summary>When set, the next batch chunk upsert fails.</summary>
    public bool FailNextChunkUpsert { get; set; }

    /// <summary>When set, the next chunk delete fails.</summary>
    public bool FailNextChunkDelete { get; set; }

    /// <summary>When set, awaited once (then cleared) before the next chunk delete runs.</summary>
    public Func<Task>? BeforeNextChunkDelete { get; set; }

    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null) =>
        inner.GetCollection<TKey, TRecord>(name, definition);

    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition definition)
    {
        var c = inner.GetDynamicCollection(name, definition);
        return new FailingCollection(this, c, name.EndsWith("_documents", StringComparison.Ordinal));
    }

    public override IAsyncEnumerable<string> ListCollectionNamesAsync(CancellationToken cancellationToken = default) =>
        inner.ListCollectionNamesAsync(cancellationToken);

    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) =>
        inner.CollectionExistsAsync(name, cancellationToken);

    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default) =>
        inner.EnsureCollectionDeletedAsync(name, cancellationToken);

    public override object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    private sealed class FailingCollection(FailingVectorStore owner, VectorStoreCollection<object, Dictionary<string, object?>> c, bool isManifest)
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

        public override async Task DeleteAsync(IEnumerable<object> keys, CancellationToken cancellationToken = default)
        {
            if (!isManifest && owner.FailNextChunkDelete)
            {
                owner.FailNextChunkDelete = false;
                throw new IOException("Simulated crash during chunk delete.");
            }

            if (!isManifest && owner.BeforeNextChunkDelete is { } gate)
            {
                owner.BeforeNextChunkDelete = null;
                await gate().ConfigureAwait(false);
            }

            await c.DeleteAsync(keys, cancellationToken).ConfigureAwait(false);
        }

        public override Task UpsertAsync(Dictionary<string, object?> record, CancellationToken cancellationToken = default)
        {
            if (isManifest && owner.FailManifestUpsertIn > 0 && --owner.FailManifestUpsertIn == 0)
                throw new IOException("Simulated crash before the manifest was written.");

            return c.UpsertAsync(record, cancellationToken);
        }

        public override Task UpsertAsync(IEnumerable<Dictionary<string, object?>> records, CancellationToken cancellationToken = default)
        {
            if (!isManifest && owner.FailNextChunkUpsert)
            {
                owner.FailNextChunkUpsert = false;
                throw new IOException("Simulated crash during chunk upsert.");
            }

            return c.UpsertAsync(records, cancellationToken);
        }

        public override async IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<Dictionary<string, object?>>? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var hit in c.SearchAsync(searchValue, top, options, cancellationToken).ConfigureAwait(false))
            {
                var record = hit.Record;
                if (owner.DropColumnOnSearch is { } col)
                {
                    record = new Dictionary<string, object?>(record);
                    record.Remove(col);
                }

                yield return new VectorSearchResult<Dictionary<string, object?>>(record, owner.NullSearchScores ? null : hit.Score);
            }
        }

        public override object? GetService(Type serviceType, object? serviceKey = null) => c.GetService(serviceType, serviceKey);
    }
}
