using System.Reflection;
using System.Runtime.InteropServices;

namespace RagDotNet.Ocr.Tesseract;

/// <summary>
/// Makes TesseractOCR find the system's Tesseract on Linux and macOS. TesseractOCR includes Windows binaries only; elsewhere
/// it looks for files named <c>x64/libleptonica-1.85.0.dll.so</c> and <c>x64/libtesseract55.dll.so</c> and loads
/// <c>libdl</c> by a name current glibc no longer ships. This creates those names as links to the installed libraries in a
/// private folder, points TesseractOCR's loader at it, and maps <c>libdl</c> to its real name. Nothing in the
/// application's folder or the system is changed.
/// </summary>
internal static class TesseractNative
{
    private static readonly object Gate = new();
    private static bool _loaded;

    private static readonly string[] LinuxDirectories =
        ["/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/local/lib"];

    private static readonly string[] MacDirectories = ["/opt/homebrew/lib", "/usr/local/lib"];

    /// <summary>Prepares native loading once per process.</summary>
    /// <exception cref="OcrUnavailableException">Tesseract or Leptonica is not installed.</exception>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded || OperatingSystem.IsWindows())
            {
                _loaded = true;
                return;
            }

            var mac = OperatingSystem.IsMacOS();
            var directories = mac ? MacDirectories : LinuxDirectories;
            var leptonica = Find(directories, mac ? ["libleptonica.dylib", "liblept.dylib"] : ["libleptonica.so.6", "liblept.so.5", "libleptonica.so", "liblept.so"]);
            var tesseract = Find(directories, mac ? ["libtesseract.dylib"] : ["libtesseract.so.5", "libtesseract.so"]);
            if (leptonica is null || tesseract is null) throw new OcrUnavailableException(InstallHint());

            var root = Path.Combine(Path.GetTempPath(), "ragdotnet-tesseract-" + Environment.UserName);
            try
            {
                foreach (var platform in new[] { "x64", "arm64" })
                {
                    var folder = Directory.CreateDirectory(Path.Combine(root, platform)).FullName;
                    Link(Path.Combine(folder, "libleptonica-1.85.0.dll.so"), leptonica);
                    Link(Path.Combine(folder, "libtesseract55.dll.so"), tesseract);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new OcrUnavailableException($"Could not prepare Tesseract's native libraries in '{root}': {ex.Message}", ex);
            }

            TesseractOCR.InteropDotNet.LibraryLoader.Instance.CustomSearchPath = root;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(TesseractOCR.Engine).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // The application set its own resolver for TesseractOCR; it is left in charge.
            }

            _loaded = true;
        }
    }

    /// <summary>What to install on this operating system.</summary>
    public static string InstallHint() =>
        OperatingSystem.IsWindows()
            ? "Tesseract's included Windows libraries could not be loaded. Install the Microsoft Visual C++ Redistributable (x64) from https://aka.ms/vs/17/release/vc_redist.x64.exe."
            : OperatingSystem.IsMacOS()
                ? "Tesseract OCR is not installed. Install it with: brew install tesseract"
                : "Tesseract OCR is not installed. Install it with: sudo apt install tesseract-ocr (Debian, Ubuntu), sudo dnf install tesseract (Fedora), or your distribution's tesseract package.";

    private static string? Find(string[] directories, string[] names) =>
        names.SelectMany(n => directories.Select(d => Path.Combine(d, n))).FirstOrDefault(File.Exists);

    private static void Link(string path, string target)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget == target) return;
        if (info.Exists || info.LinkTarget is not null) info.Delete();
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (IOException) when (new FileInfo(path).LinkTarget == target)
        {
            // Another process created the same link first.
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "libdl") return IntPtr.Zero;
        foreach (var candidate in OperatingSystem.IsMacOS() ? new[] { "/usr/lib/libSystem.B.dylib" } : ["libdl.so.2", "libc.so.6"])
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return IntPtr.Zero;
    }
}
