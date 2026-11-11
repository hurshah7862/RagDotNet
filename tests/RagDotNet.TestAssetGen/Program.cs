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
        var pdf = Path.Combine(root, "tests", "TestAssets", "pdf", "sample.pdf");
        PdfWriter.WriteSample(pdf);
        Console.WriteLine($"wrote {Path.GetRelativePath(root, pdf)}");
        return 0;
    case "formats":
        // Renders the evaluation's base set as PDF, DOCX and HTML for the format check.
        var set = Path.Combine(root, "tests", "RagDotNet.Evaluation", "Dataset", "base");
        foreach (var md in Directory.GetFiles(Path.Combine(set, "corpus"), "*.md").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(md);
            var (blocks, title) = MarkdownToGenBlocks.Convert(File.ReadAllText(md));
            PdfWriter.Write(Path.Combine(set, "formats", "pdf", id + ".pdf"), blocks, title);
            DocxWriter.Write(Path.Combine(set, "formats", "docx", id + ".docx"), blocks, title);
            HtmlWriter.Write(Path.Combine(set, "formats", "html", id + ".html"), blocks, title);
            Console.WriteLine($"wrote {id}.pdf, {id}.docx, {id}.html");
        }

        return 0;
    default:
        Console.Error.WriteLine("Usage: RagDotNet.TestAssetGen samples|formats");
        return 1;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "RagDotNet.slnx"))) return dir.FullName;
    throw new InvalidOperationException("Run inside the RagDotNet repository (no RagDotNet.slnx found above the current directory).");
}
