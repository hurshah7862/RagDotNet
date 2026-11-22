namespace RagDotNet.VectorData.Sqlite;

/// <summary>Options for <see cref="SqliteVectorStore"/>.</summary>
public sealed class SqliteVectorStoreOptions
{
    /// <summary>
    /// Full path to a sqlite-vec loadable extension. Leave it unset on win-x64, linux-x64 and osx-arm64, where the package
    /// ships sqlite-vec; set it on other platforms.
    /// </summary>
    public string? VecExtensionPath { get; set; }
}
