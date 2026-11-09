using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using RagDotNet.Documents;

namespace RagDotNet.Readers.Html;

/// <summary>
/// Reads HTML into heading, paragraph, list, table and code blocks with heading paths. Page boilerplate (navigation,
/// headers and footers outside an article, sidebars, cookie banners, forms, scripts, styles and hidden elements) is
/// removed first. No script runs and nothing is fetched over the network.
/// </summary>
public sealed partial class HtmlReader : IDocumentReader
{
    private static readonly string[] ContentTypes = ["text/html", "application/xhtml+xml"];
    private static readonly string[] Extensions = [".html", ".htm", ".xhtml"];

    private static readonly HashSet<string> RemovedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "form", "nav", "aside",
    };

    private static readonly HashSet<string> RemovedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "navigation", "banner", "contentinfo", "complementary", "search",
    };

    private static readonly string[] BoilerplateNames = ["cookie", "consent", "gdpr", "newsletter", "breadcrumb", "sidebar", "share", "skip-link"];

    /// <inheritdoc />
    public bool CanRead(string? contentType, string fileName)
    {
        if (contentType is not null)
        {
            var mediaType = contentType.Split(';')[0].Trim();
            return ContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
        }

        return Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);

        IDocument document;
        try
        {
            // AngleSharp detects the encoding like a browser (BOM, then <meta charset>, then UTF-8) and replaces bytes it
            // cannot decode, so a damaged page still yields its readable text.
            document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new DocumentReadException(context.FileName, ex.Message, ex);
        }

        RemoveBoilerplate(document);
        var root = ContentRoot(document);
        var walker = new Walker();
        if (root is not null) walker.WalkContainer(root);

        var warnings = new List<IngestWarning>();
        var images = root?.QuerySelectorAll("img").Count(i => (i.GetAttribute("src") ?? "").TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)) ?? 0;
        if (images > 0)
            warnings.Add(new IngestWarning(IngestWarningCodes.ImageNotRead, $"{images} embedded image(s) were not read; register an OCR provider to read them."));

        var title = document.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            title = walker.Blocks.FirstOrDefault(b => b.Kind == BlockKind.Heading && b.HeadingPath.Count == 1)?.Text;

        return new ParsedDocument(walker.Blocks, string.IsNullOrEmpty(title) ? null : title) { Warnings = warnings };
    }

    private static void RemoveBoilerplate(IDocument document)
    {
        var doomed = document.All.Where(IsBoilerplate).ToList();
        foreach (var element in doomed)
            element.Remove();
    }

    private static bool IsBoilerplate(IElement e)
    {
        var tag = e.LocalName;
        if (tag is "html" or "head" or "body" or "main") return false;
        if (RemovedTags.Contains(tag)) return true;
        if (tag is "header" or "footer" && e.Closest("article") is null) return true;
        if (e.HasAttribute("hidden")) return true;
        if (string.Equals(e.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (e.GetAttribute("role") is { } role && RemovedRoles.Contains(role.Trim())) return true;

        var style = e.GetAttribute("style");
        if (style is not null && style.Replace(" ", "", StringComparison.Ordinal).Contains("display:none", StringComparison.OrdinalIgnoreCase)) return true;

        var names = $"{e.Id} {e.GetAttribute("class")}";
        return BoilerplateNames.Any(n => names.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    private static IElement? ContentRoot(IDocument document)
    {
        if (document.QuerySelector("main") is { } main) return main;
        if (document.QuerySelector("[role=main]") is { } roleMain) return roleMain;
        var articles = document.QuerySelectorAll("article");
        return articles.Length == 1 ? articles[0] : document.Body;
    }

    /// <summary>Turns elements into blocks, keeping the heading path as it goes.</summary>
    private sealed class Walker
    {
        private readonly List<(int Level, string Title)> _path = [];

        public List<DocumentBlock> Blocks { get; } = [];

        public void WalkContainer(IElement container)
        {
            var loose = new StringBuilder();
            foreach (var node in container.ChildNodes)
            {
                if (node is IText text)
                {
                    loose.Append(Whitespace().Replace(text.Data, " "));
                    continue;
                }

                if (node is not IElement e) continue;
                switch (e.LocalName)
                {
                    case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                        Flush(loose);
                        AddHeading(e.LocalName[1] - '0', InlineText(e).Replace('\n', ' '));
                        break;
                    case "p" or "blockquote" or "figcaption":
                        Flush(loose);
                        Add(BlockKind.Paragraph, InlineText(e));
                        break;
                    case "ul" or "ol":
                        Flush(loose);
                        var lines = new List<string>();
                        ListLines(e, 0, lines);
                        Add(BlockKind.List, string.Join('\n', lines));
                        break;
                    case "dl":
                        Flush(loose);
                        Add(BlockKind.List, DefinitionList(e));
                        break;
                    case "table":
                        Flush(loose);
                        Add(BlockKind.Table, Table(e));
                        break;
                    case "pre":
                        Flush(loose);
                        Add(BlockKind.Code, e.TextContent.Trim('\r', '\n').TrimEnd());
                        break;
                    case "br":
                        loose.Append('\n');
                        break;
                    case "img":
                        break;
                    case "div" or "section" or "article" or "header" or "footer" or "figure" or "main" or "details" or "summary" or "address" or "center":
                        Flush(loose);
                        WalkContainer(e);
                        break;
                    default:
                        loose.Append(InlineText(e));
                        break;
                }
            }

            Flush(loose);
        }

        private void Flush(StringBuilder loose)
        {
            Add(BlockKind.Paragraph, Clean(loose.ToString()));
            loose.Clear();
        }

        private void AddHeading(int level, string title)
        {
            title = Clean(title);
            if (title.Length == 0) return;
            _path.RemoveAll(h => h.Level >= level);
            _path.Add((level, title));
            Blocks.Add(new DocumentBlock(BlockKind.Heading, title, Snapshot()));
        }

        private void Add(BlockKind kind, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Blocks.Add(new DocumentBlock(kind, text, Snapshot()));
        }

        private string[] Snapshot() => [.. _path.Select(h => h.Title)];
    }

    private static void ListLines(IElement list, int depth, List<string> lines)
    {
        foreach (var item in list.Children.Where(c => c.LocalName == "li"))
        {
            var text = InlineText(item, skipLists: true).Replace('\n', ' ');
            if (text.Length > 0) lines.Add(new string(' ', depth * 2) + "- " + text);
            foreach (var nested in item.Children.Where(c => c.LocalName is "ul" or "ol"))
                ListLines(nested, depth + 1, lines);
        }
    }

    private static string DefinitionList(IElement list)
    {
        var lines = new List<string>();
        string? term = null;
        foreach (var child in list.Children)
        {
            var text = InlineText(child).Replace('\n', ' ');
            if (child.LocalName == "dt") term = text;
            else if (child.LocalName == "dd") lines.Add(term is null ? "- " + text : $"- {term}: {text}");
        }

        return string.Join('\n', lines);
    }

    private static string Table(IElement table)
    {
        // Rows of nested tables belong to their own table and are flattened into the outer cell's text.
        var rows = table.QuerySelectorAll("tr").Where(r => r.Closest("table") == table);
        var lines = rows
            .Select(r => r.Children.Where(c => c.LocalName is "td" or "th").Select(c => InlineText(c).Replace('\n', ' ')).ToList())
            .Where(cells => cells.Count > 0)
            .Select(cells => "| " + string.Join(" | ", cells) + " |");
        return string.Join('\n', lines);
    }

    /// <summary>The element's text with whitespace collapsed to single spaces and <c>br</c> kept as a newline.</summary>
    private static string InlineText(IElement element, bool skipLists = false)
    {
        var sb = new StringBuilder();
        Append(element);
        return Clean(sb.ToString());

        void Append(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IText text) sb.Append(Whitespace().Replace(text.Data, " "));
                else if (child is IElement e)
                {
                    if (e.LocalName == "br") sb.Append('\n');
                    else if (skipLists && e.LocalName is "ul" or "ol") continue;
                    else
                    {
                        // Keep words in neighbouring cells, list items and paragraphs apart.
                        var block = e.LocalName is "p" or "div" or "li" or "td" or "th" or "tr" or "table" or "dd" or "dt";
                        if (block) sb.Append(' ');
                        Append(e);
                        if (block) sb.Append(' ');
                    }
                }
            }
        }
    }

    private static string Clean(string text) => SpacesAroundNewline().Replace(MultipleSpaces().Replace(text, " "), "\n").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex SpacesAroundNewline();
}
