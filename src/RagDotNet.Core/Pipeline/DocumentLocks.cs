namespace RagDotNet.Pipeline;

/// <summary>
/// In-process async locks keyed on (partition, document id). Entries are reference-counted and
/// removed once no caller holds or awaits them, so the map does not grow with the number of documents.
/// </summary>
internal sealed class DocumentLocks
{
    private readonly Dictionary<(string Partition, string DocumentId), Entry> _entries = [];

    /// <summary>Number of keys currently held or awaited.</summary>
    public int Count
    {
        get
        {
            lock (_entries) return _entries.Count;
        }
    }

    /// <summary>Waits for exclusive access to the document; dispose the result to release it.</summary>
    public async ValueTask<Releaser> AcquireAsync(string partition, string documentId, CancellationToken cancellationToken)
    {
        var key = (partition, documentId);
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries[key] = entry = new Entry();
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Dereference(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Dereference((string, string) key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.References == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    internal sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    /// <summary>Releases the document lock.</summary>
    internal sealed class Releaser : IDisposable
    {
        private readonly DocumentLocks _owner;
        private readonly (string, string) _key;
        private Entry? _entry;

        internal Releaser(DocumentLocks owner, (string, string) key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null) return;
            entry.Semaphore.Release();
            _owner.Dereference(_key, entry);
        }
    }
}
