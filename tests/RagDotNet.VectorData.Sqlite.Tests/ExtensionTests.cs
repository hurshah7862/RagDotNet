using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;

namespace RagDotNet.VectorData.Sqlite.Tests;

public sealed class ExtensionTests
{
    private static string ShippedNative()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        var (rid, file) =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && arch == Architecture.X64 ? ("win-x64", "vec0.dll")
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && arch == Architecture.X64 ? ("linux-x64", "vec0.so")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && arch == Architecture.Arm64 ? ("osx-arm64", "vec0.dylib")
            : throw new PlatformNotSupportedException($"No shipped sqlite-vec for {RuntimeInformation.OSDescription}/{arch}.");
        return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file);
    }

    [Fact]
    public void Shipped_native_loads_and_reports_its_version()
    {
        var native = ShippedNative();
        Assert.True(File.Exists(native), $"Missing {native}");

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.EnableExtensions(true);
        connection.LoadExtension(native);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT vec_version()";

        Assert.Equal("v0.1.9", command.ExecuteScalar());
    }

    [Fact]
    public void Store_constructs_with_the_shipped_native()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);

        var metadata = Assert.IsType<VectorStoreMetadata>(store.GetService(typeof(VectorStoreMetadata)));
        Assert.Equal("sqlite", metadata.VectorStoreSystemName);
    }

    [Fact]
    public void Missing_extension_names_the_option()
    {
        using var db = new TempDatabase();

        var e = Assert.Throws<InvalidOperationException>(() => new SqliteVectorStore(db.ConnectionString, new() { VecExtensionPath = "/nope/vec0.so" }));

        Assert.Contains("SqliteVectorStoreOptions.VecExtensionPath", e.Message);
        Assert.Contains("/nope/vec0.so", e.Message);
    }
}
