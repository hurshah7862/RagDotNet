using RagDotNet.Pipeline;

namespace RagDotNet.Embedding;

/// <summary>Fails closed when any stored document was embedded with a model other than the configured one. Only success is cached.</summary>
internal sealed class EmbeddingModelGuard(RagEngine engine)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _compatible;
    private volatile bool _modeChecked;

    /// <summary>Fails when any stored manifest was written in a different content storage mode. Only success is cached.</summary>
    public async Task EnsureContentModeAsync(CancellationToken ct)
    {
        if (_modeChecked) return;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_modeChecked) return;
            await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            var (found, stored) = await engine.Manifests.FindOtherContentModeAsync(engine.Options.ContentStorage, ct).ConfigureAwait(false);
            if (found)
                throw new InvalidOperationException(
                    $"The collection '{engine.Options.CollectionName}' stores chunk text as '{stored}' but the pipeline is configured for '{engine.Options.ContentStorage}'. Switching content storage mode on an existing collection is not supported; use a new collection.");
            _modeChecked = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task EnsureCompatibleAsync(CancellationToken ct)
    {
        await EnsureContentModeAsync(ct).ConfigureAwait(false);
        if (_compatible) return;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_compatible) return;
            await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            var (found, stored) = await engine.Manifests.FindOtherModelAsync(engine.EmbeddingModelId, ct).ConfigureAwait(false);
            if (found) throw new EmbeddingModelMismatchException(stored, engine.EmbeddingModelId);
            _compatible = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Reset() => _compatible = false;
}
