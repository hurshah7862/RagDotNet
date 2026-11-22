namespace RagDotNet.VectorData.Sqlite.Tests;

/// <summary>net8.0 has no async LINQ in the box; this is all the tests need.</summary>
internal static class AsyncList
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(ct)) list.Add(item);
        return list;
    }
}
