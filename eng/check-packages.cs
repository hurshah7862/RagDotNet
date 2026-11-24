// Checks the packages `dotnet pack` produced before anyone installs them.
// Usage: dotnet run eng/check-packages.cs -- <packageDir> [--version <x.y.z[-label]>]
// Prints one line per problem ("<PackageId>: <problem>") and exits 1 if there are any.

using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

const string RepoUrl = "https://github.com/hurshah7862/RagDotNet";
string[] expectedIds =
[
    "RagDotNet.Core", "RagDotNet.Readers.Pdf", "RagDotNet.Readers.Pdf.Rendering", "RagDotNet.Readers.Docx",
    "RagDotNet.Readers.Html", "RagDotNet.Ocr.Tesseract", "RagDotNet.Keyword.Lucene", "RagDotNet.Hosting",
    "RagDotNet.DataIngestion", "RagDotNet.VectorData.Sqlite",
];
string[] previewIds = ["RagDotNet.Keyword.Lucene", "RagDotNet.DataIngestion"];
string[] frameworks = ["net8.0", "net10.0"];

if (args.Length is not (1 or 3) || (args.Length == 3 && args[1] != "--version"))
{
    Console.Error.WriteLine("Usage: dotnet run eng/check-packages.cs -- <packageDir> [--version <x.y.z[-label]>]");
    return 2;
}
var dir = args[0];
var expectedVersion = args.Length == 3 ? args[2] : null;
if (!Directory.Exists(dir))
{
    Console.Error.WriteLine($"No such directory: {dir}");
    return 2;
}

var problems = new List<string>();
void Fail(string id, string problem) => problems.Add($"{id}: {problem}");

var packages = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
foreach (var path in Directory.GetFiles(dir, "*.nupkg").Order(StringComparer.Ordinal))
{
    var package = Package.Open(path);
    if (!packages.TryAdd(package.Id, package)) Fail(package.Id, $"more than one package ({Path.GetFileName(path)})");
}

foreach (var id in expectedIds.Where(id => !packages.ContainsKey(id))) Fail(id, "package missing");
foreach (var id in packages.Keys.Where(id => !expectedIds.Contains(id))) Fail(id, "unexpected package: only the libraries under src/ are published");

foreach (var p in packages.Values.Where(p => expectedIds.Contains(p.Id)))
{
    if (!File.Exists(Path.Combine(dir, $"{p.Id}.{p.Version}.snupkg"))) Fail(p.Id, $"no symbol package {p.Id}.{p.Version}.snupkg");

    var license = p.Meta("license");
    if (license?.Attribute("type")?.Value != "expression" || license.Value != "MIT") Fail(p.Id, "license is not the MIT expression");
    foreach (var field in new[] { "authors", "description", "tags" })
        if (string.IsNullOrWhiteSpace(p.Meta(field)?.Value)) Fail(p.Id, $"empty {field}");
    if (p.Meta("projectUrl")?.Value != RepoUrl) Fail(p.Id, $"projectUrl is not {RepoUrl}");
    var repo = p.Meta("repository");
    if (repo?.Attribute("type")?.Value != "git" || repo.Attribute("url")?.Value != RepoUrl)
        Fail(p.Id, $"repository is not git at {RepoUrl}");
    if (string.IsNullOrWhiteSpace(repo?.Attribute("commit")?.Value)) Fail(p.Id, "repository has no commit");

    foreach (var tfm in frameworks)
    foreach (var ext in new[] { "dll", "xml" })
        if (!p.Has($"lib/{tfm}/{p.Id}.{ext}")) Fail(p.Id, $"no lib/{tfm}/{p.Id}.{ext}");

    // nuget.org shows the packed README as the package page, and cannot resolve relative links in it.
    if (p.Meta("readme")?.Value != "README.md") Fail(p.Id, "nuspec names no README.md");
    if (p.Read("README.md") is not { } readme) Fail(p.Id, "no README.md in the package");
    else
        foreach (Match link in MarkdownLink().Matches(readme))
        {
            var target = link.Groups[1].Value;
            if (!target.StartsWith("http://", StringComparison.Ordinal) && !target.StartsWith("https://", StringComparison.Ordinal)
                && !target.StartsWith('#'))
                Fail(p.Id, $"README link '{target}' is relative; nuget.org cannot resolve it");
        }

    var stable = !p.Version.Contains('-');
    foreach (var (dependency, minimum) in p.Dependencies())
    {
        if (stable && minimum.Contains('-')) Fail(p.Id, $"stable package depends on prerelease {dependency} {minimum}");
        if (p.Id == "RagDotNet.Core" && minimum.Contains('-')) Fail(p.Id, $"Core depends on prerelease {dependency} {minimum}");
    }
}

if (packages.TryGetValue("RagDotNet.VectorData.Sqlite", out var sqlite))
    foreach (var entry in new[]
             {
                 "runtimes/linux-x64/native/vec0.so", "runtimes/osx-arm64/native/vec0.dylib",
                 "runtimes/win-x64/native/vec0.dll", "THIRD-PARTY-NOTICES.md",
             })
        if (!sqlite.Has(entry)) Fail(sqlite.Id, $"no {entry}");

// One shared version; the preview packages add a preview label to it.
if (packages.TryGetValue("RagDotNet.Core", out var core))
{
    if (expectedVersion is not null && core.Version != expectedVersion)
        Fail(core.Id, $"version {core.Version}, expected {expectedVersion}");
    var shared = expectedVersion ?? core.Version;
    var dash = shared.IndexOf('-');
    // The same rule as the preview targets in the Lucene and DataIngestion projects: a label that already starts with
    // "preview" is kept as it is (v1.0.0-preview.1 gives 1.0.0-preview.1), any other label goes after "preview.".
    var label = dash < 0 ? "" : shared[(dash + 1)..];
    var preview = dash < 0 ? $"{shared}-preview"
        : label.StartsWith("preview", StringComparison.Ordinal) ? shared
        : $"{shared[..dash]}-preview.{label}";
    foreach (var p in packages.Values.Where(p => expectedIds.Contains(p.Id) && p.Id != core.Id))
    {
        var want = previewIds.Contains(p.Id) ? preview : shared;
        if (p.Version != want) Fail(p.Id, $"version {p.Version}, expected {want}");
    }
}

foreach (var problem in problems) Console.WriteLine(problem);
Console.WriteLine(problems.Count == 0
    ? $"All {packages.Count} packages pass."
    : $"{problems.Count} problem(s) in {packages.Count} package(s).");
return problems.Count == 0 ? 0 : 1;

partial class Program
{
    /// <summary>The target of a Markdown link or image: <c>](target)</c> or <c>](target "title")</c>.</summary>
    [GeneratedRegex(@"\]\(\s*<?([^)\s>]+)")]
    private static partial Regex MarkdownLink();
}

sealed partial class Package
{
    private readonly string _path;
    private readonly XElement _metadata;
    private readonly HashSet<string> _entries;

    private Package(string path, string id, string version, XElement metadata, HashSet<string> entries)
    {
        _path = path;
        Id = id;
        Version = version;
        _metadata = metadata;
        _entries = entries;
    }

    public string Id { get; }

    /// <summary>The version without build metadata.</summary>
    public string Version { get; }

    public static Package Open(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nuspec = zip.Entries.Single(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = nuspec.Open();
        var metadata = XDocument.Load(stream).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
        string Value(string name) => metadata.Elements().Single(e => e.Name.LocalName == name).Value;
        return new Package(path, Value("id"), Value("version").Split('+')[0], metadata, entries);
    }

    public XElement? Meta(string name) => _metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    public bool Has(string entry) => _entries.Contains(entry);

    /// <summary>The text of an entry, or null when the package does not have it.</summary>
    public string? Read(string entry)
    {
        using var zip = ZipFile.OpenRead(_path);
        var found = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == entry);
        if (found is null) return null;
        using var reader = new StreamReader(found.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Each dependency with the minimum version of its range.</summary>
    public IEnumerable<(string Id, string Minimum)> Dependencies() =>
        _metadata.Descendants()
            .Where(e => e.Name.LocalName == "dependency")
            .Select(e => (e.Attribute("id")!.Value, MinimumOf(e.Attribute("version")?.Value ?? "")));

    private static string MinimumOf(string range) => Bracket().Replace(range, "").Split(',')[0].Trim();

    [GeneratedRegex(@"[\[\]\(\)]")]
    private static partial Regex Bracket();
}
