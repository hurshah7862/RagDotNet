using RagDotNet.Content;

namespace RagDotNet.Storage;

internal sealed record ManifestRecord(
    string Key,
    string DocumentId,
    string Partition,
    IReadOnlyDictionary<string, string> PartitionValues,
    string ContentHash,
    string ChunkingHash,
    string EmbeddingModelId,
    int ChunkCount,
    string SourceName,
    long UpdatedAtUnixMs,
    ContentStorageMode ContentStorage)
{
    public Dictionary<string, object?> ToDynamic(RagKeyType keyType)
    {
        Validate();
        var d = new Dictionary<string, object?>
        {
            ["Key"] = keyType == RagKeyType.Guid ? ChunkKeys.ToGuid(Key) : Key,
            ["DocumentId"] = DocumentId,
            ["Partition"] = Partition,
        };
        foreach (var (k, v) in PartitionValues) d[RecordDefinitions.MetaName(k)] = v;
        d["ContentHash"] = ContentHash;
        d["ChunkingHash"] = ChunkingHash;
        d["EmbeddingModelId"] = EmbeddingModelId;
        d["ChunkCount"] = ChunkCount;
        d["SourceName"] = SourceName;
        d["UpdatedAtUnixMs"] = UpdatedAtUnixMs;
        d["ContentStorage"] = ContentStorage.ToString();
        d["Placeholder"] = new ReadOnlyMemory<float>([1f]);
        return d;
    }

    private void Validate()
    {
        if (Partition != ChunkKeys.Partition(PartitionValues))
            throw new InvalidOperationException($"Manifest partition '{Partition}' does not match its partition values.");
    }

    // The key is rebuilt from partition and document id, since Guid keys are not reversible.
    public static ManifestRecord FromDynamic(Dictionary<string, object?> r, IEnumerable<string> partitionKeys)
    {
        var values = new Dictionary<string, string>();
        foreach (var k in partitionKeys)
        {
            if (!r.TryGetValue(RecordDefinitions.MetaName(k), out var v) || v is not string s)
                throw new InvalidOperationException($"Manifest record is missing the string partition value '{k}'.");
            values[k] = s;
        }
        var documentId = (string)r["DocumentId"]!;
        var partition = (string)r["Partition"]!;
        var record = new ManifestRecord(
            ChunkKeys.Document(partition, documentId), documentId, partition, values,
            (string)r["ContentHash"]!, (string)r["ChunkingHash"]!, (string)r["EmbeddingModelId"]!,
            Convert.ToInt32(r["ChunkCount"]), (string)r["SourceName"]!, Convert.ToInt64(r["UpdatedAtUnixMs"]),
            Enum.Parse<ContentStorageMode>((string)r["ContentStorage"]!));
        record.Validate();
        return record;
    }
}
