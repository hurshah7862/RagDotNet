using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RagDotNet.TestAssetGen;

/// <summary>Turns Markdown into format-neutral blocks: inline markup is dropped, text is kept.</summary>
internal static class MarkdownToGenBlocks
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    public static (List<GenBlock> Blocks, string? Title) Convert(string markdown)
    {
        var blocks = new List<GenBlock>();
        foreach (var block in Markdown.Parse(markdown, Pipeline)) Add(block, blocks);
        var title = blocks.FirstOrDefault(b => b.Kind == Documents.BlockKind.Heading && b.Level == 1)?.Text;
        return (blocks, title);
    }

    private static void Add(Block block, List<GenBlock> blocks)
    {
        switch (block)
        {
            case HeadingBlock h:
                blocks.Add(GenBlock.Heading(h.Level, Inline(h.Inline)));
                break;
            case ParagraphBlock p:
                blocks.Add(GenBlock.Paragraph(Inline(p.Inline)));
                break;
            case ListBlock list:
                var items = new List<(int, string)>();
                ListItems(list, 0, items);
                blocks.Add(GenBlock.List([.. items]));
                break;
            case Table table:
                blocks.Add(GenBlock.Table([.. table.OfType<TableRow>().Select(r => r.OfType<TableCell>().Select(c => string.Join(' ', c.OfType<ParagraphBlock>().Select(pb => Inline(pb.Inline)))).ToArray())]));
                break;
            case CodeBlock code:
                blocks.Add(GenBlock.Code(string.Join('\n', code.Lines.Lines.Take(code.Lines.Count).Select(l => l.ToString())).TrimEnd()));
                break;
            case QuoteBlock quote:
                foreach (var inner in quote) Add(inner, blocks);
                break;
        }
    }

    private static void ListItems(ListBlock list, int depth, List<(int, string)> items)
    {
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var text = string.Join(' ', item.OfType<ParagraphBlock>().Select(p => Inline(p.Inline)));
            items.Add((depth, text));
            foreach (var nested in item.OfType<ListBlock>()) ListItems(nested, depth + 1, items);
        }
    }

    private static string Inline(ContainerInline? container)
    {
        var sb = new StringBuilder();
        Append(container);
        return sb.ToString().Trim();

        void Append(ContainerInline? c)
        {
            if (c is null) return;
            foreach (var inline in c)
            {
                switch (inline)
                {
                    case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                    case CodeInline code: sb.Append(code.Content); break;
                    case LineBreakInline: sb.Append(' '); break;
                    case ContainerInline nested: Append(nested); break;
                }
            }
        }
    }
}
