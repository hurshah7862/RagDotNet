using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using RagDotNet.Documents;
using A = DocumentFormat.OpenXml.Drawing;
using V = DocumentFormat.OpenXml.Vml;

namespace RagDotNet.Readers.Docx;

/// <summary>
/// Reads DOCX documents into heading, paragraph, list, table and code blocks with heading paths. Headings come from the
/// built-in heading styles and from any style or paragraph with an outline level. Headers, footers, footnotes, comments
/// and text boxes are not read.
/// </summary>
public sealed partial class DocxReader : IDocumentReader
{
    private const string ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private static readonly string[] MonospaceFonts = ["Consolas", "Courier New", "Cascadia"];

    /// <inheritdoc />
    public bool CanRead(string? contentType, string fileName) =>
        contentType is not null
            ? string.Equals(contentType.Split(';')[0].Trim(), ContentType, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetExtension(fileName), ".docx", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);

        // The OpenXml package needs a seekable stream.
        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        WordprocessingDocument doc;
        try
        {
            doc = WordprocessingDocument.Open(buffer, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Corrupt, truncated and encrypted files all fail here, as packaging or format exceptions.
            throw new DocumentReadException(context.FileName, UnreadableReason, ex);
        }

        using (doc)
        {
            try
            {
                return Read(doc, cancellationToken);
            }
            catch (Exception ex) when (IsDamage(ex))
            {
                // Part XML is parsed lazily, on first access, so a zip that opens can still hold damaged XML.
                throw new DocumentReadException(context.FileName, UnreadableReason, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not DocumentReadException)
            {
                // The package opened and its XML parsed, so this is content the reader did not expect.
                throw new DocumentReadException(context.FileName, $"unexpected content in the document: {ex.Message}", ex);
            }
        }
    }

    private const string UnreadableReason = "the file is not a readable DOCX document (it may be corrupt or password-protected)";

    /// <summary>Damaged XML or packaging anywhere in the exception chain.</summary>
    private static bool IsDamage(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is System.Xml.XmlException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException) return true;
        return false;
    }

    private static ParsedDocument Read(WordprocessingDocument doc, CancellationToken ct)
    {
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("The document has no body.");
        var styles = new StyleIndex(doc.MainDocumentPart!.StyleDefinitionsPart?.Styles);
        var builder = new BlockBuilder();
        var images = 0;
        string? title = null;

        foreach (var element in BodyElements(body))
        {
            ct.ThrowIfCancellationRequested();
            images += CountImages(element);

            if (element is Table table)
            {
                builder.Add(BlockKind.Table, TableText(table));
                continue;
            }

            if (element is not Paragraph paragraph) continue;
            var text = ParagraphText(paragraph);
            var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;

            // Table-of-contents entries and its "Contents" heading repeat the headings with page numbers; the headings themselves are read.
            if (styles.IsTableOfContents(styleId)) continue;

            if (styles.HeadingLevel(paragraph, styleId) is { } level)
            {
                if (styles.IsTitle(styleId) && title is null && text.Length > 0) title = text;
                builder.AddHeading(level, text.Replace('\n', ' '));
            }
            else if (styles.ListLevel(paragraph, styleId) is { } listLevel)
            {
                builder.AddListItem(listLevel, text.Replace('\n', ' '));
            }
            else if (styles.IsCode(paragraph, styleId))
            {
                builder.AddCodeLine(text);
            }
            else
            {
                builder.Add(BlockKind.Paragraph, text);
            }
        }

        title ??= string.IsNullOrWhiteSpace(doc.PackageProperties.Title) ? null : doc.PackageProperties.Title!.Trim();
        var warnings = new List<IngestWarning>();
        if (images > 0)
            warnings.Add(new IngestWarning(IngestWarningCodes.ImageNotRead, $"{images} embedded image(s) were not read; register an OCR provider to read them."));
        return new ParsedDocument(builder.Finish(), title) { Warnings = warnings };
    }

    /// <summary>Body paragraphs and tables in order, looking inside content controls.</summary>
    private static IEnumerable<OpenXmlElement> BodyElements(OpenXmlElement container)
    {
        foreach (var child in container.ChildElements)
        {
            if (child is SdtBlock sdt && sdt.SdtContentBlock is { } content)
            {
                foreach (var inner in BodyElements(content)) yield return inner;
            }
            else if (child is Paragraph or Table)
            {
                yield return child;
            }
        }
    }

    private static int CountImages(OpenXmlElement element) =>
        element.Descendants<Drawing>().Count(d => d.Descendants<A.Blip>().Any())
        + element.Descendants<Picture>().Count(p => p.Descendants<V.ImageData>().Any());

    /// <summary>
    /// Visible text: tabs as spaces, breaks as newlines; deleted text (w:delText) is never a <see cref="Text"/>. Text
    /// boxes are not read: Word stores each one twice (a modern copy and a VML fallback) inside the anchoring paragraph,
    /// so including them would glue duplicated text onto that paragraph.
    /// </summary>
    private static string ParagraphText(OpenXmlElement paragraph)
    {
        var sb = new StringBuilder();
        foreach (var e in paragraph.Descendants())
        {
            if (InTextBox(e)) continue;
            switch (e)
            {
                case Text t when t.Ancestors<DeletedRun>().FirstOrDefault() is null:
                    sb.Append(t.Text);
                    break;
                case TabChar:
                    sb.Append(' ');
                    break;
                case NoBreakHyphen:
                    sb.Append('-');
                    break;
                case Break or CarriageReturn:
                    sb.Append('\n');
                    break;
            }
        }

        return SpacesAroundNewline().Replace(MultipleSpaces().Replace(sb.ToString(), " "), "\n").Trim();
    }

    private static bool InTextBox(OpenXmlElement e) => e.Ancestors<TextBoxContent>().Any();

    private static string TableText(Table table)
    {
        // Rows can sit inside content controls (w:sdt); rows of nested tables belong to those tables.
        var lines = table.Descendants<TableRow>().Where(row => row.Ancestors<Table>().First() == table)
            .Select(row => row.Elements<TableCell>()
                .Select(cell => string.Join(' ', cell.Descendants<Paragraph>().Where(p => !InTextBox(p)).Select(ParagraphText).Where(t => t.Length > 0)).Replace('\n', ' '))
                .ToList())
            .Where(cells => cells.Count > 0 && cells.Any(c => c.Length > 0))
            .Select(cells => "| " + string.Join(" | ", cells) + " |");
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex SpacesAroundNewline();

    /// <summary>Looks up heading, list and code properties through each style's based-on chain.</summary>
    private sealed partial class StyleIndex
    {
        private readonly Dictionary<string, Style> _byId = new(StringComparer.OrdinalIgnoreCase);

        public StyleIndex(Styles? styles)
        {
            foreach (var style in styles?.Elements<Style>() ?? [])
                if (style.StyleId?.Value is { } id) _byId.TryAdd(id, style);
        }

        public bool IsTitle(string? styleId) =>
            styleId is not null && (string.Equals(styleId, "Title", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Name(styleId), "Title", StringComparison.OrdinalIgnoreCase));

        public int? HeadingLevel(Paragraph paragraph, string? styleId)
        {
            if (paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value is { } own && own < 9) return own + 1;
            if (IsTitle(styleId)) return 1;
            foreach (var style in Chain(styleId))
            {
                if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } level && level < 9) return level + 1;
                var m = HeadingId().Match(style.StyleId?.Value ?? "");
                if (!m.Success) m = HeadingId().Match((style.StyleName?.Val?.Value ?? "").Replace(" ", "", StringComparison.Ordinal));
                if (m.Success) return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            // A document without a styles part can still name the built-in heading styles.
            if (styleId is not null && !_byId.ContainsKey(styleId) && HeadingId().Match(styleId) is { Success: true } byId)
                return int.Parse(byId.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            return null;
        }

        public bool IsTableOfContents(string? styleId) =>
            styleId is not null && (TocId().IsMatch(styleId) || TocId().IsMatch((Name(styleId) ?? "").Replace(" ", "", StringComparison.Ordinal)));

        /// <summary>
        /// The list level, when the paragraph is numbered. The numbering id and the level can come from the paragraph, its
        /// style chain, or one from each (a paragraph often sets only a level on a numbered style).
        /// </summary>
        public int? ListLevel(Paragraph paragraph, string? styleId)
        {
            var own = paragraph.ParagraphProperties?.NumberingProperties;
            var fromStyle = Chain(styleId).Select(s => s.StyleParagraphProperties?.NumberingProperties).FirstOrDefault(n => n is not null);
            var numId = own?.NumberingId?.Val?.Value ?? fromStyle?.NumberingId?.Val?.Value;
            if (numId is null or 0) return null;
            return own?.NumberingLevelReference?.Val?.Value ?? fromStyle?.NumberingLevelReference?.Val?.Value ?? 0;
        }

        public bool IsCode(Paragraph paragraph, string? styleId)
        {
            // "Code" must be a whole word of the style id or name: "Source Code" and "HTMLCode" count, "Barcode" does not.
            if (Chain(styleId).Any(s => HasWordCode(s.StyleId?.Value) || HasWordCode(s.StyleName?.Val?.Value)))
                return true;

            var runs = paragraph.Descendants<Run>().Where(r => r.Descendants<Text>().Any(t => t.Text.Length > 0)).ToList();
            return runs.Count > 0 && runs.All(r => IsMonospace(r.RunProperties?.RunFonts?.Ascii?.Value));
        }

        private static bool IsMonospace(string? font) =>
            font is not null && (MonospaceFonts.Any(m => font.StartsWith(m, StringComparison.OrdinalIgnoreCase))
                || font.Contains("Mono", StringComparison.OrdinalIgnoreCase));

        private static bool HasWordCode(string? name) =>
            name is not null && Words().Matches(name).Any(m => string.Equals(m.Value, "code", StringComparison.OrdinalIgnoreCase));

        private string? Name(string styleId) => _byId.TryGetValue(styleId, out var s) ? s.StyleName?.Val?.Value : null;

        private IEnumerable<Style> Chain(string? styleId)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (styleId is not null && seen.Add(styleId) && _byId.TryGetValue(styleId, out var style))
            {
                yield return style;
                styleId = style.BasedOn?.Val?.Value;
            }
        }

        [GeneratedRegex(@"^(?i:heading)([1-9])$")]
        private static partial Regex HeadingId();

        [GeneratedRegex(@"^(?i:toc)(?:[1-9]|(?i:heading))$")]
        private static partial Regex TocId();

        /// <summary>Words of a style id or name: split at spaces, punctuation and lower-to-upper case changes.</summary>
        [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
        private static partial Regex Words();
    }

    /// <summary>Collects blocks, grouping consecutive list items and code lines, and keeps the heading path.</summary>
    private sealed class BlockBuilder
    {
        private readonly List<DocumentBlock> _blocks = [];
        private readonly List<(int Level, string Title)> _path = [];
        private readonly List<string> _list = [];
        private readonly List<string> _code = [];

        public void AddHeading(int level, string title)
        {
            FlushGroups();
            if (title.Length == 0) return;
            _path.RemoveAll(h => h.Level >= level);
            _path.Add((level, title));
            _blocks.Add(new DocumentBlock(BlockKind.Heading, title, Snapshot()));
        }

        public void AddListItem(int level, string text)
        {
            FlushCode();
            if (text.Length > 0) _list.Add(new string(' ', level * 2) + "- " + text);
        }

        public void AddCodeLine(string line)
        {
            FlushList();
            _code.Add(line);
        }

        public void Add(BlockKind kind, string text)
        {
            FlushGroups();
            if (!string.IsNullOrWhiteSpace(text)) _blocks.Add(new DocumentBlock(kind, text, Snapshot()));
        }

        public List<DocumentBlock> Finish()
        {
            FlushGroups();
            return _blocks;
        }

        private void FlushGroups()
        {
            FlushList();
            FlushCode();
        }

        private void FlushList()
        {
            if (_list.Count == 0) return;
            _blocks.Add(new DocumentBlock(BlockKind.List, string.Join('\n', _list), Snapshot()));
            _list.Clear();
        }

        private void FlushCode()
        {
            if (_code.Count == 0) return;
            var text = string.Join('\n', _code).Trim('\n');
            if (text.Length > 0) _blocks.Add(new DocumentBlock(BlockKind.Code, text, Snapshot()));
            _code.Clear();
        }

        private string[] Snapshot() => [.. _path.Select(h => h.Title)];
    }
}
