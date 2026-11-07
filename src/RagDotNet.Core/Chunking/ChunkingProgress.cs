namespace RagDotNet.Chunking;

/// <summary>
/// Lets a built-in chunker that does slow work, such as the semantic chunker's sentence embeddings, report progress to
/// the ingest that called it without widening <see cref="IChunker"/>. The ingest sets a reporter for the duration of the
/// chunking call; the value flows with the async call.
/// </summary>
internal static class ChunkingProgress
{
    private static readonly AsyncLocal<Action<int, int>?> Current = new();

    /// <summary>Routes <see cref="Report"/> calls to <paramref name="report"/> until the returned scope is disposed.</summary>
    public static IDisposable Use(Action<int, int>? report)
    {
        var previous = Current.Value;
        Current.Value = report;
        return new Scope(previous);
    }

    /// <summary>Reports <paramref name="done"/> of <paramref name="total"/> units of chunking work; ignored with no reporter.</summary>
    public static void Report(int done, int total) => Current.Value?.Invoke(done, total);

    private sealed class Scope(Action<int, int>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
