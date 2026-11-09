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

    /// <summary>Never content: removed wherever they appear.</summary>
    private static readonly HashSet<string> RemovedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "nav",
        "input", "select", "option", "textarea", "button", "label",
    };

    private static readonly HashSet<string> RemovedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "navigation", "banner", "contentinfo", "complementary", "search",
    };

    /// <summary>
    /// Whole class names and ids that mark page furniture. Matching is on the exact name, never a substring, so that
    /// names such as <c>has-sidebar</c>, <c>share-price</c> or <c>cookie-recipes</c> are not mistaken for boilerplate.
    /// </summary>
    private static readonly HashSet<string> BoilerplateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cookie-banner", "cookie-consent", "cookie-notice", "cookie-bar", "cookies", "consent-banner", "gdpr-banner",
        "newsletter", "newsletter-signup", "breadcrumb", "breadcrumbs", "sidebar", "share-buttons", "social-share",
        "sharing", "skip-link", "skip-to-content",
    };

    private const string BlockSelector = "h1, h2, h3, h4, h5, h6, p, div, section, article, ul, ol, dl, table, pre, blockquote, figure";

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

        // Choose the content root before removing anything, so no rule can remove the root or what contains it.
        var root = ContentRoot(document);
        if (root is not null) RemoveBoilerplate(root, pageLevel: root == document.Body);
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

    /// <summary>
    /// Removes boilerplate inside <paramref name="root"/> (the root itself is never removed). Page-level furniture
    /// (aside, header and footer, forms without content) only counts when the root is the whole body; inside a
    /// <c>main</c> or <c>article</c>, those elements are content, such as an article's own header.
    /// </summary>
    private static void RemoveBoilerplate(IElement root, bool pageLevel)
    {
        var doomed = root.Descendants<IElement>().Where(e => IsBoilerplate(e, pageLevel)).ToList();
        foreach (var element in doomed)
            element.Remove();
    }

    private static bool IsBoilerplate(IElement e, bool pageLevel)
    {
        var tag = e.LocalName;
        if (tag is "main" or "article") return false;
        if (RemovedTags.Contains(tag)) return true;
        if (IsHidden(e)) return true;
        if (e.GetAttribute("role") is { } role && RemovedRoles.Contains(role.Trim())) return true;
        if (BoilerplateNames.Contains(e.Id ?? "") || (e.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(BoilerplateNames.Contains))
            return true;

        if (!pageLevel) return false;
        if (tag == "aside") return true;
        if (tag is "header" or "footer") return e.Closest("article, section, main") is null;

        // A form that holds headings or paragraphs is content: ASP.NET WebForms and SharePoint wrap the whole page in one.
        return tag == "form" && e.QuerySelector("h1, h2, h3, h4, h5, h6, p, table, article, main, section") is null;
    }

    private static bool IsHidden(IElement e)
    {
        // hidden="until-found" content is findable on the page, so it counts as visible.
        if (e.GetAttribute("hidden") is { } hidden && !string.Equals(hidden.Trim(), "until-found", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(e.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)) return true;
        var style = e.GetAttribute("style");
        return style is not null && style.Replace(" ", "", StringComparison.Ordinal).Contains("display:none", StringComparison.OrdinalIgnoreCase);
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
                    case "div" or "section" or "article" or "header" or "footer" or "figure" or "main" or "details" or "summary" or "address" or "center" or "aside" or "form":
                        Flush(loose);
                        WalkContainer(e);
                        break;
                    default:
                        // An inline element holding blocks (a card link around a heading and text) is walked as a
                        // container, so its heading stays a heading.
                        if (e.QuerySelector(BlockSelector) is not null)
                        {
                            Flush(loose);
                            WalkContainer(e);
                        }
                        else
                        {
                            loose.Append(InlineText(e));
                        }

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

            // Nested lists can sit inside a div within the item; take those whose nearest item is this one.
            foreach (var nested in item.QuerySelectorAll("ul, ol").Where(l => l.ParentElement?.Closest("li") == item))
                ListLines(nested, depth + 1, lines);
        }
    }

    private static string DefinitionList(IElement list)
    {
        var lines = new List<string>();
        string? term = null;
        // dt/dd pairs may be grouped in div elements, which HTML allows.
        foreach (var child in list.Children.SelectMany(c => c.LocalName == "div" ? c.Children.AsEnumerable() : [c]))
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
            .Select(r => r.Children.Where(c => c.LocalName is "td" or "th").SelectMany(Cells).ToList())
            .Where(cells => cells.Count > 0)
            .Select(cells => "| " + string.Join(" | ", cells) + " |");
        return string.Join('\n', lines);
    }

    /// <summary>A cell's text, followed by an empty cell for each extra column it spans, so later columns stay aligned.</summary>
    private static IEnumerable<string> Cells(IElement cell)
    {
        yield return InlineText(cell).Replace('\n', ' ');
        var span = int.TryParse(cell.GetAttribute("colspan"), out var n) ? Math.Clamp(n, 1, 50) : 1;
        for (var i = 1; i < span; i++) yield return "";
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
