using RagDotNet.TestAssetGen;

// Writes binary test files into the repository. Run from anywhere inside the repo:
//   dotnet run --project tests/RagDotNet.TestAssetGen -- samples
var root = FindRepoRoot();
var command = args.Length == 1 ? args[0] : "";
switch (command)
{
    case "samples":
        var docx = Path.Combine(root, "tests", "TestAssets", "docx", "sample.docx");
        DocxWriter.WriteSample(docx);
        Console.WriteLine($"wrote {Path.GetRelativePath(root, docx)}");
        return 0;
    default:
        Console.Error.WriteLine("Usage: RagDotNet.TestAssetGen samples");
        return 1;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "RagDotNet.slnx"))) return dir.FullName;
    throw new InvalidOperationException("Run inside the RagDotNet repository (no RagDotNet.slnx found above the current directory).");
}
