namespace RagDotNet.Keyword;

/// <summary>
/// Keeps keyword-index rebuilds apart from ingest and delete. Ingest and delete hold it shared from their keyword write
/// through the manifest commit, so a rebuild, which holds it exclusively, never reads a manifest whose chunks are still
/// being written and never swaps in an index that misses a write made while it ran. Writers waiting to enter shared
/// wait while a rebuild holds or waits for exclusive.
/// </summary>
internal sealed class KeywordSyncGate
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly object _sync = new();
    private int _shared;
    private TaskCompletionSource? _drained;

    /// <summary>Enters shared; many holders at once. Dispose the result to leave.</summary>
    public async Task<IDisposable> EnterSharedAsync(CancellationToken cancellationToken)
    {
        // Passing through the turnstile, which an exclusive holder keeps, is what makes writers wait for a rebuild.
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync) _shared++;
        _turnstile.Release();
        return new Releaser(this, exclusive: false);
    }

    /// <summary>Enters exclusive once every shared holder has left. Dispose the result to leave.</summary>
    public async Task<IDisposable> EnterExclusiveAsync(CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task drained;
        lock (_sync)
        {
            if (_shared == 0)
            {
                drained = Task.CompletedTask;
            }
            else
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _drained.Task;
            }
        }

        try
        {
            await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync) _drained = null;
            _turnstile.Release();
            throw;
        }

        return new Releaser(this, exclusive: true);
    }

    private void ExitShared()
    {
        lock (_sync)
        {
            if (--_shared > 0 || _drained is null) return;
            _drained.TrySetResult();
            _drained = null;
        }
    }

    private sealed class Releaser(KeywordSyncGate gate, bool exclusive) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1) return;
            if (exclusive) gate._turnstile.Release();
            else gate.ExitShared();
        }
    }
}
