using System.Runtime.InteropServices;

namespace RagDotNet.VectorData.Sqlite;

/// <summary>Finds the sqlite-vec loadable extension for the running platform.</summary>
internal static class VecExtension
{
    public static string Resolve(SqliteVectorStoreOptions options)
    {
        if (!string.IsNullOrEmpty(options.VecExtensionPath))
        {
            if (File.Exists(options.VecExtensionPath)) return options.VecExtensionPath;
            throw new InvalidOperationException(
                $"sqlite-vec was not found at '{options.VecExtensionPath}' (SqliteVectorStoreOptions.VecExtensionPath). " +
                "Set it to the full path of a sqlite-vec loadable extension.");
        }

        var (rid, file) = Platform();
        if (rid is not null)
        {
            foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file), Path.Combine(AppContext.BaseDirectory, file) })
                if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            $"sqlite-vec was not found for {RuntimeInformation.OSDescription}/{RuntimeInformation.ProcessArchitecture}. " +
            "RagDotNet.VectorData.Sqlite ships it for win-x64, linux-x64 and osx-arm64; on other platforms set " +
            "SqliteVectorStoreOptions.VecExtensionPath to a sqlite-vec loadable extension.");
    }

    private static (string? Rid, string File) Platform()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return (arch == Architecture.X64 ? "win-x64" : null, "vec0.dll");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return (arch == Architecture.X64 ? "linux-x64" : null, "vec0.so");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return (arch == Architecture.Arm64 ? "osx-arm64" : null, "vec0.dylib");
        return (null, "vec0");
    }
}
