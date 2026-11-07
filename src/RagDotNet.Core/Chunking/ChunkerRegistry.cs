namespace RagDotNet.Chunking;

/// <summary>Finds chunkers by name: the built-in ones plus those the app registered.</summary>
internal sealed class ChunkerRegistry
{
    private readonly Dictionary<string, IChunker> _byName = new(StringComparer.Ordinal);

    public ChunkerRegistry(IEnumerable<IChunker> appChunkers, IEnumerable<IChunker> builtIns)
    {
        foreach (var builtIn in builtIns) _byName[builtIn.Name] = builtIn;
        foreach (var c in appChunkers) _byName[c.Name] = c;
    }

    public IChunker Get(string name) =>
        name is not null && _byName.TryGetValue(name, out var chunker)
            ? chunker
            : throw new ArgumentException($"No chunker named '{name}' is registered. Register it with AddChunker.");
}
