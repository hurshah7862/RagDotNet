using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;

namespace RagDotNet.StoreTests.Infrastructure;

/// <summary>
/// One vector store the shared suite runs against. A fixture that needs a service (a container, a server) starts it in
/// <see cref="InitializeAsync"/> and sets <see cref="UnavailableReason"/> when it cannot.
/// </summary>
public abstract class StoreFixture : IAsyncLifetime
{
    /// <summary>The store's name in test output and gate messages, for example "InMemory" or "Qdrant".</summary>
    public abstract string Name { get; }

    /// <summary>True when the store offers native hybrid search, so retrieval reports the "native" hybrid path instead of "index".</summary>
    public virtual bool NativeHybrid => false;

    /// <summary>Why the store could not start, or null when it is available.</summary>
    public string? UnavailableReason { get; protected set; }

    /// <summary>
    /// A store instance for one test; the suite disposes it after the test. Each test also uses its own collection name.
    /// A store over a shared service may return a light wrapper over one shared client.
    /// </summary>
    public abstract VectorStore CreateStore();

    /// <summary>
    /// The store's own settings (spec §2.2), applied to every pipeline the suite builds before the test's own settings.
    /// Every override must match a setting documented for the store in docs/vector-stores.md.
    /// </summary>
    public virtual void Configure(RagOptions options)
    {
    }

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
