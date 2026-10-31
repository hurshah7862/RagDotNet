namespace RagDotNet.Content;

/// <summary>App-supplied storage for chunk text when <see cref="ContentStorageMode.External"/> is used.</summary>
public interface IChunkContentStore
{
    /// <summary>Saves or replaces the text of the given chunks.</summary>
    /// <param name="chunks">The chunks to save.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the chunks are saved.</returns>
    Task UpsertAsync(IReadOnlyList<ChunkContent> chunks, CancellationToken ct);

    /// <summary>Fetches chunk text by key. Keys with no stored text are omitted from the result.</summary>
    /// <param name="chunkKeys">The chunk keys.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The text of each found chunk, by key.</returns>
    Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct);

    /// <summary>Deletes chunk text by key. Keys that do not exist are ignored.</summary>
    /// <param name="chunkKeys">The chunk keys.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the chunks are deleted.</returns>
    Task DeleteAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct);
}
