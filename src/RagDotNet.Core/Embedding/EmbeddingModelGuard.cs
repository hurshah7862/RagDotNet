using RagDotNet.Pipeline;

namespace RagDotNet.Embedding;

/// <summary>Fails closed when any stored document was embedded with a model other than the configured one. Only success is cached.</summary>
internal sealed class EmbeddingModelGuard(RagEngine engine)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _compatible;

    public async Task EnsureCompatibleAsync(CancellationToken ct)
    {
        if (_compatible) return;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_compatible) return;
            await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            var stored = await engine.Manifests.FindOtherModelIdAsync(engine.EmbeddingModelId, ct).ConfigureAwait(false);
            if (stored is not null) throw new EmbeddingModelMismatchException(stored, engine.EmbeddingModelId);
            _compatible = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Reset() => _compatible = false;
}
