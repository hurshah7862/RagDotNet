using Microsoft.Data.Sqlite;

namespace RagDotNet.VectorData.Sqlite.Tests;

/// <summary>A database file in its own temp directory, deleted on dispose.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ragdotnet-sqlite-" + Guid.NewGuid().ToString("N"));

    public TempDatabase()
    {
        Directory.CreateDirectory(_dir);
        Path = System.IO.Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
    }

    public string Path { get; }

    public string ConnectionString => "Data Source=" + Path;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
