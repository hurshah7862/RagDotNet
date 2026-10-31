using RagDotNet.Documents;

namespace RagDotNet.Readers;

internal sealed class ReaderSelector(IEnumerable<IDocumentReader> readers)
{
    private readonly IDocumentReader[] _readers = readers.ToArray();

    public IDocumentReader Select(string? contentType, string fileName)
    {
        foreach (var reader in _readers)
        {
            if (reader.CanRead(contentType, fileName))
            {
                return reader;
            }
        }

        throw new UnsupportedDocumentException(fileName, contentType);
    }
}
