using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using RagDotNet.Chunking;
using RagDotNet.Documents;

namespace RagDotNet.DataIngestion;

/// <summary>
/// Uses a Microsoft.Extensions.DataIngestion <see cref="IngestionChunker{T}"/> as a RagDotNet <see cref="IChunker"/>. Each
/// chunk's heading path comes from the chunker's context when it names a heading of the document (as <c>HeaderChunker</c>'s
/// does), and is empty otherwise. Chunks carry no page numbers, because Microsoft's chunks do not.
/// </summary>
public sealed class DataIngestionChunker : IChunker
{
    private static readonly Lazy<Tokenizer> DefaultTokenizer = new(() => TiktokenTokenizer.CreateForEncoding("cl100k_base"));

    private readonly Func<ChunkingOptions, IngestionChunker<string>> _createChunker;

    /// <summary>Creates the adapter.</summary>
    /// <param name="name">The name <see cref="ChunkingOptions.Chunker"/> selects this chunker by.</param>
    /// <param name="version">The chunker's version; changing it re-chunks documents on their next ingest.</param>
    /// <param name="createChunker">
    /// Creates the Microsoft chunker for each document from the app's chunking options; <see cref="CreateOptions"/> turns
    /// those into Microsoft's.
    /// </param>
    public DataIngestionChunker(string name, string version, Func<ChunkingOptions, IngestionChunker<string>> createChunker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(createChunker);
        Name = name;
        Version = version;
        _createChunker = createChunker;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Version { get; }

    /// <summary>
    /// Microsoft's chunker options for RagDotNet's: <see cref="ChunkingOptions.MaxTokens"/> per chunk, and an overlap of
    /// <see cref="ChunkingOptions.OverlapRatio"/> of that, kept below the maximum as Microsoft's options require.
    /// </summary>
    /// <param name="options">The app's chunking options.</param>
    /// <param name="tokenizer">The tokenizer that counts tokens; by default cl100k_base, the one RagDotNet counts with.</param>
    public static IngestionChunkerOptions CreateOptions(ChunkingOptions options, Tokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var overlap = Math.Max(0, Math.Min((int)(options.MaxTokens * options.OverlapRatio), options.MaxTokens - 1));
        return new IngestionChunkerOptions(tokenizer ?? DefaultTokenizer.Value)
        {
            MaxTokensPerChunk = options.MaxTokens,
            OverlapTokens = overlap,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var (ingestion, paths) = ToIngestionDocument(document);
        var chunker = _createChunker(options);
        var chunks = new List<Chunk>();
        await foreach (var chunk in chunker.ProcessAsync(ingestion, cancellationToken).ConfigureAwait(false))
        {
            var context = chunk.Context ?? string.Empty;
            var text = chunk.Content ?? string.Empty;
            if (context.Length > 0 && text.StartsWith(context + "\n", StringComparison.Ordinal)) text = text[(context.Length + 1)..];
            var path = paths.TryGetValue(context, out var p) ? p : [];
            chunks.Add(new Chunk(chunks.Count, text.Trim(), path, PageStart: null, PageEnd: null, TokenCount: 0));
        }

        return chunks;
    }

    /// <summary>
    /// The document as Microsoft's, plus a lookup from each heading's context string (the markdown of the headings on its
    /// path, joined by spaces, as <c>HeaderChunker</c> builds it) to its heading path, so the path is found, never parsed.
    /// </summary>
    private static (IngestionDocument Document, Dictionary<string, IReadOnlyList<string>> Paths) ToIngestionDocument(ParsedDocument document)
    {
        var section = new IngestionDocumentSection();
        var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var stack = new List<(int Level, string Markdown)>();
        foreach (var block in document.Blocks)
        {
            if (block.Kind == BlockKind.Heading && block.HeadingPath.Count > 0)
            {
                var level = block.HeadingPath.Count;
                stack.RemoveAll(h => h.Level >= level);
                stack.Add((level, block.Text));
                paths.TryAdd(string.Join(" ", stack.Select(h => h.Markdown)), block.HeadingPath);
                section.Elements.Add(new IngestionDocumentHeader(block.Text)
                {
                    Level = level,
                    Text = block.HeadingPath[^1],
                    PageNumber = block.PageNumber,
                });
            }
            else
            {
                section.Elements.Add(new IngestionDocumentParagraph(block.Text) { Text = block.Text, PageNumber = block.PageNumber });
            }
        }

        var ingestion = new IngestionDocument(document.Title ?? "document");
        ingestion.Sections.Add(section);
        return (ingestion, paths);
    }
}
