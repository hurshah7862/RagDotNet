using RagDotNet.Pipeline;
using RagDotNet.Storage;

namespace RagDotNet.Embedding;

/// <summary>What the caller is about to do; decides how a reindex in progress is treated.</summary>
internal enum GuardMode
{
    Read,
    Write,
}

/// <summary>
/// Fails closed when stored data is on a model other than the configured one. The observed state (reindex marker and
/// any document on another model) is cached for <see cref="Configuration.RagOptions.ModelCheckInterval"/>, whatever the
/// outcome, and re-fetched after it expires or after <see cref="Reset"/>. Shared by every pipeline over the engine.
/// </summary>
internal sealed class EmbeddingModelGuard(RagEngine engine)
{
    /// <summary>One observation of the store. <paramref name="OtherModelId"/> is null when every document is on the configured model.</summary>
    private sealed record State(ReindexMarker? Marker, string? OtherModelId, DateTimeOffset FetchedAt);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _modeChecked;
    private volatile State? _state;
    private long _generation;
    private string? _warnedOwner;

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

    /// <summary>
    /// Checks that <paramref name="mode"/> may proceed. Returns true while a reindex to the configured model is in progress:
    /// reads must then be limited to chunks already on the configured model.
    /// </summary>
    /// <exception cref="EmbeddingModelMismatchException">A reindex to another model is in progress, or stored data is on another model.</exception>
    public async ValueTask<bool> EnsureAsync(GuardMode mode, CancellationToken ct)
    {
        await EnsureContentModeAsync(ct).ConfigureAwait(false);
        var state = _state;
        if (state is null || IsExpired(state)) state = await RefreshAsync(ct).ConfigureAwait(false);
        return Decide(state);
    }

    /// <summary>
    /// True once per reindex marker (keyed on its owner) while a reindex to the configured model is in progress, so the
    /// "reads use re-embedded data" warning is logged once per process per marker. Re-armed once no marker is seen.
    /// </summary>
    public bool TakeReindexWarning()
    {
        var owner = _state?.Marker?.OwnerId;
        if (owner is null) return false;
        return !string.Equals(Interlocked.Exchange(ref _warnedOwner, owner), owner, StringComparison.Ordinal);
    }

    /// <summary>Discards the cached state, so the next check reads the store.</summary>
    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        _state = null;
    }

    // A clock that stepped backwards counts as expired, so a correction can never freeze the cached state.
    private bool IsExpired(State state)
    {
        var elapsed = engine.Time.GetUtcNow() - state.FetchedAt;
        return elapsed < TimeSpan.Zero || elapsed >= engine.Options.ModelCheckInterval;
    }

    private async Task<State> RefreshAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _state;
            if (current is not null && !IsExpired(current)) return current;

            var generation = Interlocked.Read(ref _generation);
            await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            // Stamped before the reads: the observation is at least this old, so the cache never outlives ModelCheckInterval
            // measured from the moment the store was looked at.
            var fetchedAt = engine.Time.GetUtcNow();
            var marker = await engine.Manifests.GetMarkerAsync(ct).ConfigureAwait(false);
            string? other = null;
            if (marker is null)
            {
                var (found, stored) = await engine.Manifests.FindOtherModelAsync(engine.EmbeddingModelId, ct).ConfigureAwait(false);
                if (found) other = stored;
                Volatile.Write(ref _warnedOwner, null); // re-arm the warning for the next reindex
            }

            var state = new State(marker, other, fetchedAt);
            // A Reset() during the fetch means the store changed under it; use this observation once but do not cache it.
            if (Interlocked.Read(ref _generation) == generation) _state = state;
            return state;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool Decide(State state)
    {
        var configured = engine.EmbeddingModelId;
        if (state.Marker is { } marker)
        {
            if (!string.Equals(marker.TargetModelId, configured, StringComparison.Ordinal))
                throw new EmbeddingModelMismatchException(marker.TargetModelId, configured);
            return true;
        }

        if (state.OtherModelId is { } stored) throw new EmbeddingModelMismatchException(stored, configured);
        return false;
    }
}
