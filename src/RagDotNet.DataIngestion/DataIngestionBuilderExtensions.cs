using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Documents;

namespace RagDotNet.DataIngestion;

/// <summary>Registers Microsoft.Extensions.DataIngestion readers and chunkers with RagDotNet.</summary>
public static class DataIngestionBuilderExtensions
{
    /// <summary>
    /// Adds a Microsoft.Extensions.DataIngestion reader for the given extensions and content types. App readers are tried before
    /// RagDotNet's built-in ones, so this one takes over its extensions from them.
    /// </summary>
    /// <param name="builder">The RagDotNet builder.</param>
    /// <param name="createReader">Creates the reader, once.</param>
    /// <param name="fileExtensions">File extensions it handles, with their dot, such as <c>.md</c>.</param>
    /// <param name="contentTypes">Content types it handles, such as <c>text/markdown</c>.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">Neither an extension nor a content type is given.</exception>
    public static IRagBuilder AddDataIngestionReader(
        this IRagBuilder builder,
        Func<IServiceProvider, IngestionDocumentReader> createReader,
        IEnumerable<string> fileExtensions,
        IEnumerable<string>? contentTypes = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createReader);
        ArgumentNullException.ThrowIfNull(fileExtensions);
        var extensions = fileExtensions.ToArray();
        var types = contentTypes?.ToArray() ?? [];
        if (!extensions.Any(e => !string.IsNullOrWhiteSpace(e)) && !types.Any(t => !string.IsNullOrWhiteSpace(t)))
        {
            throw new ArgumentException("Give at least one file extension or content type for the reader to handle.", nameof(fileExtensions));
        }

        builder.Services.AddSingleton<IDocumentReader>(sp => new DataIngestionReader(createReader(sp), extensions, types));
        return builder;
    }

    /// <summary>Adds a Microsoft.Extensions.DataIngestion chunker, selected by <see cref="ChunkingOptions.Chunker"/> = <paramref name="name"/>.</summary>
    /// <param name="builder">The RagDotNet builder.</param>
    /// <param name="name">The chunker's name.</param>
    /// <param name="version">The chunker's version; changing it re-chunks documents on their next ingest.</param>
    /// <param name="createChunker">
    /// Creates the Microsoft chunker for each document from the app's chunking options; <see cref="DataIngestionChunker.CreateOptions"/>
    /// turns those into Microsoft's.
    /// </param>
    /// <returns>The builder.</returns>
    public static IRagBuilder AddDataIngestionChunker(
        this IRagBuilder builder,
        string name,
        string version,
        Func<IServiceProvider, ChunkingOptions, IngestionChunker<string>> createChunker)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(createChunker);
        builder.Services.AddSingleton<IChunker>(sp => new DataIngestionChunker(name, version, options => createChunker(sp, options)));
        return builder;
    }
}
