using System.Text;
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
        var cursor = 0;
        await foreach (var chunk in chunker.ProcessAsync(ingestion, cancellationToken).ConfigureAwait(false))
        {
            var context = chunk.Context ?? string.Empty;
            var text = chunk.Content ?? string.Empty;
            // HeaderChunker ends the context line with Environment.NewLine: \r\n on Windows, \n elsewhere.
            if (context.Length > 0 && text.StartsWith(context, StringComparison.Ordinal))
            {
                var rest = text.AsSpan(context.Length);
                if (rest.StartsWith("\r\n", StringComparison.Ordinal)) text = text[(context.Length + 2)..];
                else if (rest.StartsWith("\n", StringComparison.Ordinal)) text = text[(context.Length + 1)..];
            }
            var path = FindPath(paths, context, text, ref cursor);
            chunks.Add(new Chunk(chunks.Count, text.Trim(), path, PageStart: null, PageEnd: null, TokenCount: 0));
        }

        return chunks;
    }

    /// <summary>
    /// The heading path for a chunk's context. Chunkers emit chunks in document order and a long section gives several
    /// chunks, so the first heading with that context at or after the previous match is taken; one found only earlier
    /// (a chunker that emits out of order) is taken next. Two headings with different paths can read the same (a heading
    /// named "A ## B" and the chain "A" then "B"): for such an ambiguous context the section holding the chunk's whole
    /// text wins, then the one holding its first line, searching from the previous match onwards and then before it.
    /// Only ambiguous contexts compare text, so a long section is not searched once per chunk. No match gives an empty path.
    /// </summary>
    private static IReadOnlyList<string> FindPath(List<HeadingEntry> paths, string context, string text, ref int cursor)
    {
        var after = -1;
        for (var i = cursor; i < paths.Count && after < 0; i++)
            if (paths[i].Key == context) after = i;
        var before = -1;
        if (after < 0)
            for (var i = 0; i < cursor && before < 0; i++)
                if (paths[i].Key == context) before = i;

        var found = after >= 0 ? after : before;
        if (found < 0) return [];
        if (!paths[found].Ambiguous)
        {
            if (after >= 0) cursor = after;
            return paths[found].Path;
        }

        // Ambiguous: let the text decide. HeaderChunker may join blocks with \r\n; the section text uses \n.
        var body = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var firstLine = body.Split('\n', 2)[0].Trim();
        foreach (var needle in new[] { body, firstLine })
        {
            if (needle.Length == 0) continue;
            for (var n = 0; n < paths.Count; n++)
            {
                var i = (cursor + n) % paths.Count; // from the previous match onwards, then the ones before it
                if (paths[i].Key != context || !paths[i].Text.Contains(needle, StringComparison.Ordinal)) continue;
                cursor = i;
                return paths[i].Path;
            }
        }

        if (after >= 0) cursor = after;
        return paths[found].Path;
    }

    /// <summary>
    /// A heading's context string and path, the text of the blocks under it (up to the next heading), and whether another
    /// heading with a different path has the same context.
    /// </summary>
    private sealed class HeadingEntry(string key, IReadOnlyList<string> path)
    {
        private StringBuilder? _body = new();

        public string Key { get; } = key;

        public IReadOnlyList<string> Path { get; } = path;

        public string Text { get; private set; } = string.Empty;

        public bool Ambiguous { get; private set; }

        // Blocks keep the source's line endings (\r\n in Windows-authored files); chunk text is compared with \n.
        public void Append(string blockText) => _body!.Append(blockText.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');

        /// <summary>Called once the document is read: keeps the text as one string.</summary>
        public void Complete(bool ambiguous)
        {
            Text = _body!.ToString();
            _body = null;
            Ambiguous = ambiguous;
        }
    }

    /// <summary>
    /// The document as Microsoft's, plus each heading's context string (the markdown of the headings on its path, joined
    /// by spaces, as <c>HeaderChunker</c> builds it) with its heading path, in document order, so the path is found,
    /// never parsed.
    /// </summary>
    private static (IngestionDocument Document, List<HeadingEntry> Paths) ToIngestionDocument(ParsedDocument document)
    {
        var section = new IngestionDocumentSection();
        var paths = new List<HeadingEntry>();
        var stack = new List<(int Level, string Markdown)>();
        foreach (var block in document.Blocks)
        {
            if (block.Kind == BlockKind.Heading && block.HeadingPath.Count > 0)
            {
                var level = block.HeadingPath.Count;
                stack.RemoveAll(h => h.Level >= level);
                stack.Add((level, block.Text));
                paths.Add(new HeadingEntry(string.Join(" ", stack.Select(h => h.Markdown)), block.HeadingPath));
                section.Elements.Add(new IngestionDocumentHeader(block.Text)
                {
                    Level = level,
                    Text = block.HeadingPath[^1],
                    PageNumber = block.PageNumber,
                });
            }
            else
            {
                if (paths.Count > 0) paths[^1].Append(block.Text);
                section.Elements.Add(new IngestionDocumentParagraph(block.Text) { Text = block.Text, PageNumber = block.PageNumber });
            }
        }

        // A context is ambiguous when headings with different paths share it; only those need the chunk text to choose.
        var ambiguous = paths
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .Where(g => g.Select(e => string.Join('\u001f', e.Path)).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in paths) entry.Complete(ambiguous.Contains(entry.Key));

        var ingestion = new IngestionDocument(document.Title ?? "document");
        ingestion.Sections.Add(section);
        return (ingestion, paths);
    }
}
