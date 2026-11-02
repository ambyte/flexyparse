using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Syntax;

namespace FlexyParse.Markdown;

internal sealed class MarkdownBlockJsonRenderer : MarkdownObjectRenderer<MarkdownJsonRenderer, Block>
{
    protected override void Write(MarkdownJsonRenderer renderer, Block block)
    {
        renderer.CheckCancellation();
        var paragraphText = block is ParagraphBlock paragraph ? renderer.InlineText(paragraph) : null;
        if (paragraphText is { Length: 0 })
        {
            return;
        }

        var writer = renderer.Writer;
        writer.WriteStartObject();
        switch (block)
        {
            case HeadingBlock heading:
                writer.WriteString("type", "heading");
                writer.WriteNumber("level", heading.Level);
                writer.WriteString("text", renderer.InlineText(heading));
                break;
            case ParagraphBlock:
                writer.WriteString("type", "paragraph");
                writer.WriteString("text", paragraphText);
                break;
            case CodeBlock code:
                writer.WriteString("type", "code");
                if (code is FencedCodeBlock fenced && !string.IsNullOrEmpty(fenced.Info))
                {
                    writer.WriteString("language", fenced.Info);
                }

                writer.WriteString("text", renderer.CodeText(code));
                break;
            case ListBlock list:
                writer.WriteString("type", "list");
                if (list.IsOrdered)
                {
                    writer.WriteBoolean("ordered", true);
                    if (list.OrderedStart != "1")
                    {
                        writer.WriteString("start", list.OrderedStart);
                    }
                }

                writer.WriteStartArray("items");
                foreach (var item in list.Cast<ListItemBlock>())
                {
                    renderer.WriteBlocks(item);
                }

                writer.WriteEndArray();
                break;
            case QuoteBlock quote:
                writer.WriteString("type", "quote");
                writer.WritePropertyName("blocks");
                renderer.WriteBlocks(quote);
                break;
            case Table table when IsSimpleTable(table):
                writer.WriteString("type", "table");
                if (table.ColumnDefinitions.Any(column => column.Alignment is not null))
                {
                    writer.WriteStartArray("alignments");
                    foreach (var column in table.ColumnDefinitions)
                    {
                        writer.WriteStringValue(column.Alignment?.ToString().ToLowerInvariant());
                    }

                    writer.WriteEndArray();
                }

                writer.WriteStartArray("rows");
                foreach (var row in table.Cast<TableRow>())
                {
                    renderer.CheckCancellation();
                    writer.WriteStartArray();
                    foreach (var cell in row.Cast<TableCell>())
                    {
                        writer.WriteStringValue(cell.Count == 0 ? "" : renderer.InlineText((LeafBlock)cell[0]));
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                break;
            case ThematicBreakBlock:
                writer.WriteString("type", "thematicBreak");
                break;
            default:
                // Complex tables, HTML, math and extension blocks remain lossless Markdown.
                writer.WriteString("type", "markdown");
                writer.WriteString("text", renderer.SourceText(block));
                break;
        }

        writer.WriteEndObject();
    }

    private static bool IsSimpleTable(Table table) => table.Cast<TableRow>().All(row =>
        row.IsHeader == (table.IndexOf(row) == 0)
        && row.Cast<TableCell>().All(cell => cell.ColumnSpan == 1 && cell.RowSpan == 1
            && (cell.Count == 0 || (cell.Count == 1 && cell[0] is ParagraphBlock))));
}
