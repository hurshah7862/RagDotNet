using System.Security;
using System.Text;
using RagDotNet.Retrieval;

namespace RagDotNet.Generation;

/// <summary>
/// The numbered <c>&lt;document&gt;</c> tags retrieved chunks are shown to a model in, shared by the default prompt and the
/// search tool. Attribute values are XML-escaped and document text cannot close or open a tag.
/// </summary>
internal static class DocumentTags
{
    public static StringBuilder Append(StringBuilder sb, int number, RetrievedChunk chunk)
    {
        sb.Append("<document id=\"").Append(number).Append("\" source=\"").Append(Attr(chunk.SourceName)).Append('"');
        if (chunk.PageStart is { } start)
        {
            sb.Append(" page=\"").Append(PageLocation.Format(start, chunk.PageEnd)).Append('"');
        }
        else if (chunk.HeadingPath.Count > 0)
        {
            sb.Append(" section=\"").Append(Attr(string.Join(" > ", chunk.HeadingPath))).Append('"');
        }

        return sb.Append(">\n").Append(EscapeText(chunk.Text)).Append("\n</document>\n");
    }

    /// <summary>A page range, heading path, or source name locating the chunk, as <see cref="Citation.Location"/> reports it.</summary>
    public static string Locate(RetrievedChunk chunk)
    {
        if (chunk.PageStart is { } start) return PageLocation.Format(start, chunk.PageEnd);
        return chunk.HeadingPath.Count > 0 ? string.Join(" > ", chunk.HeadingPath) : chunk.SourceName;
    }

    private static string EscapeText(string text) =>
        text.Replace("</document", "&lt;/document", StringComparison.OrdinalIgnoreCase)
            .Replace("<document", "&lt;document", StringComparison.OrdinalIgnoreCase);

    private static string Attr(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
