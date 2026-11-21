using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;
using static RagDotNet.Keyword.Lucene.LuceneFilterTranslator;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace RagDotNet.Keyword.Lucene;

/// <summary>
/// A keyword index persisted on disk with Lucene.NET, ranked with BM25 and analysed with <c>StandardAnalyzer</c>. Chunk
/// text is indexed but not stored. One process (and one instance) can open a directory at a time. Dispose it to release
/// the directory; with dependency injection the container does so.
/// </summary>
public sealed class LuceneKeywordIndex : IKeywordIndex, IDisposable
{
    /// <summary>Lucene's default limit on clauses in one boolean query.</summary>
    internal const int MaxQueryTerms = 1024;

    private readonly LuceneDirectory _directory;
    private readonly Analyzer _analyzer;
    private readonly IndexWriter _writer;
    private readonly SearcherManager _searchers;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    /// <summary>Opens or creates the index.</summary>
    /// <param name="options">The index directory.</param>
    /// <exception cref="ArgumentException"><see cref="LuceneKeywordIndexOptions.Directory"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Another process or index instance has the directory open.</exception>
    public LuceneKeywordIndex(LuceneKeywordIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Directory))
            throw new ArgumentException("LuceneKeywordIndexOptions.Directory is required.", nameof(options));

        var path = Path.GetFullPath(options.Directory);
        System.IO.Directory.CreateDirectory(path);
        _directory = FSDirectory.Open(path);
        _analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);
        try
        {
            _writer = new IndexWriter(_directory, new IndexWriterConfig(LuceneVersion.LUCENE_48, _analyzer)
            {
                OpenMode = OpenMode.CREATE_OR_APPEND,
                Similarity = Bm25, // Lucene 4.8 defaults to classic TF-IDF
            });
        }
        catch (LockObtainFailedException ex)
        {
            _analyzer.Dispose();
            _directory.Dispose();
            throw new InvalidOperationException(LockedMessage(options.Directory), ex);
        }

        _writer.Commit(); // a new directory gets an empty commit, so searchers can open it
        _searchers = new SearcherManager(_writer, applyAllDeletes: true, new Bm25SearcherFactory());
    }

    /// <summary>BM25 with the same parameters as the in-memory index.</summary>
    private static readonly BM25Similarity Bm25 = new(1.2f, 0.75f);

    private sealed class Bm25SearcherFactory : SearcherFactory
    {
        public override IndexSearcher NewSearcher(IndexReader reader) => new(reader) { Similarity = Bm25 };
    }

    internal static string LockedMessage(string directory) =>
        $"The Lucene keyword index directory '{directory}' is in use by another process or index instance.";

    /// <inheritdoc />
    public string Name => "lucene";

    /// <inheritdoc />
    public Task UpsertAsync(IReadOnlyList<KeywordChunk> chunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        return WriteAsync(() =>
        {
            foreach (var chunk in chunks) _writer.UpdateDocument(new Term(KeyField, chunk.ChunkKey), ToDocument(chunk));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(IReadOnlyCollection<string> chunkKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunkKeys);
        return WriteAsync(() => _writer.DeleteDocuments(chunkKeys.Select(k => new Term(KeyField, k)).ToArray()), cancellationToken);
    }

    /// <inheritdoc />
    public async Task RebuildAsync(IAsyncEnumerable<KeywordChunk> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                _writer.DeleteAll();
                await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
                    _writer.AddDocument(ToDocument(chunk));
            }
            catch
            {
                // Nothing of a failed rebuild becomes visible: the last commit stays.
                _writer.Rollback();
                throw;
            }

            Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KeywordHit>> SearchAsync(string query, KeywordFilter filter, int top, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        cancellationToken.ThrowIfCancellationRequested();

        var terms = Terms(query);
        if (terms.Count == 0) return Task.FromResult<IReadOnlyList<KeywordHit>>([]);

        // Each term is a plain SHOULD clause: user text never reaches Lucene's query parser.
        var text = new BooleanQuery();
        foreach (var term in terms) text.Add(new TermQuery(new Term(TextFieldName, term)), Occur.SHOULD);
        // The filter only selects: as a Filter it adds nothing to the score, so partition and metadata terms never reorder hits.
        var constraint = Translate(filter) is { } c ? new QueryWrapperFilter(c) : null;

        var searcher = _searchers.Acquire();
        try
        {
            var found = searcher.Search(text, constraint, top);
            IReadOnlyList<KeywordHit> hits = found.ScoreDocs
                .Select(d => new KeywordHit(searcher.Doc(d.Doc).Get(KeyField), d.Score))
                .ToList();
            return Task.FromResult(hits);
        }
        finally
        {
            _searchers.Release(searcher);
        }
    }

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        var searcher = _searchers.Acquire();
        try
        {
            return Task.FromResult((long)searcher.IndexReader.NumDocs);
        }
        finally
        {
            _searchers.Release(searcher);
        }
    }

    /// <summary>Commits and releases the directory. Calls afterwards throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _writeGate.Wait();
        try
        {
            _searchers.Dispose();
            _writer.Dispose();
            _analyzer.Dispose();
            _directory.Dispose();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>The distinct analysed terms of the query, at most <see cref="MaxQueryTerms"/>.</summary>
    internal List<string> Terms(string query)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var stream = _analyzer.GetTokenStream(TextFieldName, new StringReader(query));
        var attribute = stream.AddAttribute<ICharTermAttribute>();
        stream.Reset();
        while (terms.Count < MaxQueryTerms && stream.IncrementToken())
        {
            var term = attribute.ToString();
            if (seen.Add(term)) terms.Add(term);
        }

        stream.End();
        return terms;
    }

    private async Task WriteAsync(Action write, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            write();
            Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Commit()
    {
        _writer.Commit();
        _searchers.MaybeRefreshBlocking();
    }

    private static Document ToDocument(KeywordChunk chunk)
    {
        var doc = new Document
        {
            new StringField(KeyField, chunk.ChunkKey, Field.Store.YES),
            new StringField(DocumentField, chunk.DocumentId, Field.Store.NO),
            new StringField(PartitionField, PartitionTerm(chunk.Partition), Field.Store.NO),
            new TextField(TextFieldName, chunk.Text, Field.Store.NO),
        };
        foreach (var (name, value) in chunk.Metadata)
        {
            switch (value)
            {
                case null:
                    break;
                case string[] values:
                    foreach (var v in values) doc.Add(new StringField(MetaPrefix + name, v, Field.Store.NO));
                    break;
                default:
                    doc.Add(new StringField(MetaPrefix + name, Encode(value), Field.Store.NO));
                    break;
            }
        }

        return doc;
    }
}
