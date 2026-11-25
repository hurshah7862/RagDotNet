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
    var symbols = Path.Combine(dir, $"{p.Id}.{p.Version}.snupkg");
    if (!File.Exists(symbols)) Fail(p.Id, $"no symbol package {p.Id}.{p.Version}.snupkg");
    else
    {
        using var zip = ZipFile.OpenRead(symbols);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tfm in frameworks)
            if (!entries.Contains($"lib/{tfm}/{p.Id}.pdb")) Fail(p.Id, $"symbol package has no lib/{tfm}/{p.Id}.pdb");
    }

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

    // nuget.org shows the packed README as the package page, with the rules of its MarkdownService (see Readme below).
    if (p.Meta("readme")?.Value != "README.md") Fail(p.Id, "nuspec names no README.md");
    if (p.Read("README.md") is not { } readme) Fail(p.Id, "no README.md in the package");
    else foreach (var problem in Readme.Problems(readme)) Fail(p.Id, problem);

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

/// <summary>
/// What nuget.org does to a package README (NuGetGallery's MarkdownService, PackageHelper and ImageDomainValidator): it
/// removes HTML comments before anything else, renders the Markdown with raw HTML disabled (a tag is shown as text), keeps
/// only links to absolute http or https URLs and #anchors, and shows only images from its trusted domains.
/// </summary>
static partial class Readme
{
    /// <summary>
    /// The image domains nuget.org trusts, as listed at https://learn.microsoft.com/nuget/nuget-org/package-readme-on-nuget-org
    /// (read 2026-11-25; nuget.org's live list may change). A host matches one exactly, ignoring case; a two-part domain
    /// also matches with "www.". GitHub workflow badges are allowed separately (<see cref="GitHubBadge"/>).
    /// </summary>
    private static readonly HashSet<string> TrustedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.codacy.com", "api.codeclimate.com", "api.dependabot.com", "api.reuse.software", "api.travis-ci.com",
        "app.codacy.com", "app.deepsource.com", "avatars.githubusercontent.com", "badgen.net", "badges.gitter.im",
        "camo.githubusercontent.com", "caniuse.bitsofco.de", "cdn.jsdelivr.net", "cdn.syncfusion.com", "ci.appveyor.com",
        "circleci.com", "cloudback.it", "codecov.io", "codefactor.io", "coveralls.io", "dev.azure.com", "devpod.sh",
        "flat.badgen.net", "gitlab.com", "i.imgur.com", "img.shields.io", "infragistics.com", "isitmaintained.com",
        "media.githubusercontent.com", "opencollective.com", "raw.github.com", "raw.githubusercontent.com", "snyk.io",
        "sonarcloud.io", "travis-ci.com", "travis-ci.org", "user-images.githubusercontent.com",
    };

    /// <summary>Each way the README would show differently on nuget.org than its author meant.</summary>
    public static IEnumerable<string> Problems(string readme)
    {
        // nuget.org removes comments before it parses anything, so a comment hides what it covers, even inside code. Code
        // is shown, not linked: drop fenced blocks, and replace inline code with a placeholder (not nothing, so a link
        // label written as code, [`Foo`]: target, is still a label).
        var prose = InlineCode().Replace(FencedCode().Replace(NuGetComment().Replace(readme, ""), ""), "code");

        foreach (Match tag in HtmlTagWithUrl().Matches(prose))
            yield return $"README has an HTML <{tag.Groups["tag"].Value}> tag; nuget.org shows raw HTML as text";

        var images = InlineImage().Matches(prose).Select(m => m.Groups["url"]).ToList();
        var imageTargets = images.Select(g => g.Value).ToList();
        var imageStarts = images.Select(g => g.Index).ToHashSet();
        var linkTargets = MarkdownLink().Matches(prose).Select(m => m.Groups[1])
            .Where(g => !imageStarts.Contains(g.Index)).Select(g => g.Value).ToList();
        // A reference definition is an image's when any image uses its label: ![alt][label], ![label][] or ![label].
        var imageLabels = ReferenceImage().Matches(prose)
            .Select(m => Label(m.Groups["ref"].Value is { Length: > 0 } label ? label : m.Groups["alt"].Value)).ToHashSet();
        foreach (Match definition in ReferenceLink().Matches(prose))
            (imageLabels.Contains(Label(definition.Groups["label"].Value)) ? imageTargets : linkTargets).Add(definition.Groups["url"].Value);
        foreach (Match autolink in Autolink().Matches(prose))
            linkTargets.Add(autolink.Groups["email"].Success ? $"mailto:{autolink.Groups["email"].Value}" : autolink.Groups["uri"].Value);

        foreach (var target in linkTargets.Where(t => !t.StartsWith('#') && !IsWebUrl(t, out _)))
            yield return $"README link '{target}' is not an http or https URL; nuget.org removes it";
        foreach (var target in imageTargets.Where(t => !IsWebUrl(t, out var uri) || !(IsTrustedHost(uri.Host) || GitHubBadge().IsMatch(t))))
            yield return $"README image '{target}' is not on a domain nuget.org shows images from";
    }

    private static bool IsWebUrl(string target, out Uri uri) =>
        Uri.TryCreate(target, UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsTrustedHost(string host) =>
        TrustedImageHosts.Contains(host)
        || (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && host.Count(c => c == '.') == 2 && TrustedImageHosts.Contains(host[4..]));

    /// <summary>A reference label as Markdown matches it: case and runs of whitespace do not count.</summary>
    private static string Label(string label) =>
        string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>An HTML comment, exactly as nuget.org removes it: up to the next <c>--&gt;</c>, anywhere.</summary>
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex NuGetComment();

    /// <summary>The target of a Markdown link or image: <c>](target)</c> or <c>](target "title")</c>.</summary>
    [GeneratedRegex(@"\]\(\s*<?([^)\s>]+)")]
    private static partial Regex MarkdownLink();

    /// <summary>An inline image, <c>![alt](target)</c>, whose alt text may hold one level of brackets.</summary>
    [GeneratedRegex(@"!\[(?:[^\[\]]|\[[^\[\]]*\])*\]\(\s*<?(?<url>[^)\s>]+)")]
    private static partial Regex InlineImage();

    /// <summary>A reference image: <c>![alt][label]</c>, <c>![label][]</c> or <c>![label]</c>.</summary>
    [GeneratedRegex(@"!\[(?<alt>[^\[\]]*)\](?:\[(?<ref>[^\[\]]*)\])?(?!\()")]
    private static partial Regex ReferenceImage();

    /// <summary>
    /// A reference definition, <c>[label]: target</c> at the start of a line, also inside a blockquote. A footnote
    /// (<c>[^1]: text</c>) is not a link.
    /// </summary>
    [GeneratedRegex(@"^ {0,3}(?:>[ \t]? {0,3})*\[(?!\^)(?<label>[^\]]+)\]:[ \t]*(?:\r?\n(?:[ \t]*>)*[ \t]*)?<?(?<url>[^\s>]+)", RegexOptions.Multiline)]
    private static partial Regex ReferenceLink();

    /// <summary>An autolink: <c>&lt;scheme:target&gt;</c>, or <c>&lt;name@example.com&gt;</c>, which is a mailto link.</summary>
    [GeneratedRegex(@"<(?:(?<uri>[a-zA-Z][a-zA-Z0-9+.-]{1,31}:[^\s<>]*)|(?<email>[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*))>")]
    private static partial Regex Autolink();

    /// <summary>
    /// An HTML tag with a URL attribute (<c>src</c>, <c>href</c> or <c>srcset</c>), such as an image or a link written in
    /// HTML. The attribute must follow whitespace, a slash or a closing quote, so <c>data-src</c> is another attribute.
    /// </summary>
    [GeneratedRegex(@"<(?<tag>[a-zA-Z][a-zA-Z0-9-]*)(?=[\s/>])[^<>]*?[\s/""'](?:src|href|srcset)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagWithUrl();

    /// <summary>A GitHub Actions workflow badge, which nuget.org shows from github.com (its own pattern).</summary>
    [GeneratedRegex(@"^(https|http):\/\/github\.com\/[^/]+\/[^/]+(\/actions)?\/workflows\/.*badge\.svg", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubBadge();

    /// <summary>
    /// A fenced code block, up to a closing fence of the same character at least as long as the opening one, or the end of
    /// the file. A backtick fence's info string cannot contain a backtick, so <c>```js``` text</c> is not a fence.
    /// </summary>
    [GeneratedRegex(@"^ {0,3}(?:(`{3,})[^`\n]*|(~{3,})[^\n]*)\n[\s\S]*?(?:^ {0,3}(?:\1`*|\2~*)[ \t]*\r?$|\z)", RegexOptions.Multiline)]
    private static partial Regex FencedCode();

    /// <summary>
    /// An inline code span: a run of backticks, up to the next run of the same length within the paragraph. A span never
    /// crosses a blank line, so a stray backtick cannot hide the rest of the file.
    /// </summary>
    [GeneratedRegex(@"(`+)(?:(?!\r?\n[ \t]*\r?\n)[\s\S])*?(?<!`)\1(?!`)")]
    private static partial Regex InlineCode();
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
