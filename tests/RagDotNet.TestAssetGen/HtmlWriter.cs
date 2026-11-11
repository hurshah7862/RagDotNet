using System.Net;
using System.Text;
using RagDotNet.Documents;

namespace RagDotNet.TestAssetGen;

/// <summary>
/// Writes a full HTML page: navigation, a cookie banner and a footer around a &lt;main&gt; holding the blocks, so that
/// reading it back also exercises boilerplate removal.
/// </summary>
internal static class HtmlWriter
{
    public static void Write(string path, IReadOnlyList<GenBlock> blocks, string? title)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<title>").Append(E(title ?? "Quill help")).Append(" | Quill Help Centre</title>\n")
          .Append("<script>window.dataLayer = [];</script>\n</head>\n<body>\n")
          .Append("<header><nav><a href=\"/\">Quill</a> <a href=\"/help\">Help Centre</a> <a href=\"/download\">Download</a></nav></header>\n")
          .Append("<div class=\"cookie-banner\">We use cookies to understand how the help centre is used. <button>Accept</button></div>\n")
          .Append("<main>\n");

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case BlockKind.Heading:
                    var level = Math.Clamp(block.Level, 1, 6);
                    sb.Append($"<h{level}>").Append(E(block.Text)).Append($"</h{level}>\n");
                    break;
                case BlockKind.Paragraph:
                    sb.Append("<p>").Append(E(block.Text)).Append("</p>\n");
                    break;
                case BlockKind.Code:
                    sb.Append("<pre><code>").Append(E(block.Text)).Append("</code></pre>\n");
                    break;
                case BlockKind.List:
                    List(sb, block.Items!, 0, 0);
                    break;
                case BlockKind.Table:
                    sb.Append("<table>\n<thead><tr>").Append(string.Concat(block.Rows![0].Select(c => $"<th>{E(c)}</th>"))).Append("</tr></thead>\n<tbody>\n");
                    foreach (var row in block.Rows.Skip(1))
                        sb.Append("<tr>").Append(string.Concat(row.Select(c => $"<td>{E(c)}</td>"))).Append("</tr>\n");
                    sb.Append("</tbody>\n</table>\n");
                    break;
            }
        }

        sb.Append("</main>\n<aside><h2>Related articles</h2><ul><li><a href=\"/help/start\">Getting started</a></li></ul></aside>\n")
          .Append("<footer>Copyright 2026 Quill Labs. All rights reserved.</footer>\n</body>\n</html>\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Writes items[start..] at <paramref name="depth"/>; returns the index after the last item written.</summary>
    private static int List(StringBuilder sb, IReadOnlyList<(int Depth, string Text)> items, int start, int depth)
    {
        sb.Append("<ul>\n");
        var i = start;
        while (i < items.Count && items[i].Depth == depth)
        {
            sb.Append("<li>").Append(E(items[i].Text));
            i++;
            if (i < items.Count && items[i].Depth > depth) i = List(sb, items, i, depth + 1);
            sb.Append("</li>\n");
        }

        sb.Append("</ul>\n");
        return i;
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
