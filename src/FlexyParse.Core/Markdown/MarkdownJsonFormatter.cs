using System.Globalization;
using System.Text.Json;
using TagBites.Text.Markdown;

namespace FlexyParse.Markdown;

/// <summary>Generates Markdown from compact JSON blocks using TagBites.Text.Markdown.</summary>
public sealed class MarkdownJsonFormatter
{
    public void AppendTo(MarkdownContentElement destination, JsonElement blocks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (blocks.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Markdown content must be an array of JSON blocks.");
        }

        foreach (var block in blocks.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (block.GetProperty("type").GetString())
            {
                case "heading":
                    destination.AddHeader(Math.Clamp(block.GetProperty("level").GetInt32(), 1, 6), RawText(block));
                    break;
                case "paragraph" or "markdown":
                    destination.AddParagraph(RawText(block));
                    break;
                case "code":
                    destination.AddCode(block.TryGetProperty("language", out var language) ? language.GetString()! : "",
                        block.GetProperty("text").GetString()!);
                    break;
                case "thematicBreak":
                    destination.AddThematicBreak();
                    break;
                case "quote":
                    AppendTo(destination.AddQuote(), block.GetProperty("blocks"), cancellationToken);
                    break;
                case "list":
                    AppendList(destination, block, cancellationToken);
                    break;
                case "table":
                    AppendTable(destination, block, cancellationToken);
                    break;
                case "error":
                    destination.AddQuote(block.GetProperty("message").GetString()!);
                    break;
                case "image":
                    destination.AddParagraph(MarkdownText.Raw(block.TryGetProperty("src", out var source)
                        ? "![Image](" + source.GetString() + ")"
                        : "<!-- image -->"));
                    break;
                default:
                    throw new InvalidDataException($"Unsupported Markdown block type: {block.GetProperty("type").GetString()}.");
            }
        }
    }

    private void AppendList(MarkdownContentElement destination, JsonElement block, CancellationToken cancellationToken)
    {
        var list = destination.AddList(block.TryGetProperty("ordered", out var ordered) && ordered.GetBoolean());
        if (block.TryGetProperty("start", out var start))
        {
            list.StartNumber = int.Parse(start.GetString()!, CultureInfo.InvariantCulture);
        }

        foreach (var item in block.GetProperty("items").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = new MarkdownDocument();
            AppendTo(content, item, cancellationToken);
            list.AddItem(MarkdownText.Raw(content.ToString().TrimEnd()));
        }
    }

    private static void AppendTable(MarkdownContentElement destination, JsonElement block, CancellationToken cancellationToken)
    {
        var rows = block.GetProperty("rows");
        if (rows.GetArrayLength() == 0)
        {
            return;
        }

        if (block.TryGetProperty("firstRowIsHeader", out var header) && !header.GetBoolean())
        {
            AppendFieldList(destination, rows, cancellationToken);
            return;
        }

        var width = rows.EnumerateArray().Max(row => row.GetArrayLength());
        if (width == 0)
        {
            return;
        }

        var table = destination.AddTable();
        table.SetHeaders(Cells(rows[0], width));
        foreach (var row in rows.EnumerateArray().Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            table.WithRow(Cells(row, width));
        }

        if (block.TryGetProperty("alignments", out var alignments))
        {
            table.SetAlignments(alignments.EnumerateArray().Select(alignment =>
                Enum.Parse<MarkdownTableColumnAlignment>(alignment.GetString() ?? "None", true)).ToArray());
        }
    }

    private static void AppendFieldList(MarkdownContentElement destination, JsonElement rows, CancellationToken cancellationToken)
    {
        MarkdownList? list = null;
        foreach (var row in rows.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cells = row.EnumerateArray().Select(cell => cell.GetString()!.Trim()).ToArray();
            var key = cells.Length > 0 ? cells[0] : "";
            var value = string.Join(' ', cells.Skip(1));
            if (key.Length == 0 && value.Length == 0)
            {
                continue;
            }

            list ??= destination.AddList(false);
            var text = key.Length == 0 ? MarkdownText.Raw(value)
                : value.Length == 0 ? MarkdownText.Bold(MarkdownText.Raw(key))
                : MarkdownText.Bold(MarkdownText.Raw(key + ":")) + " " + MarkdownText.Raw(value);
            list.AddItem(text);
        }
    }

    private static MarkdownText RawText(JsonElement block) => MarkdownText.Raw(block.GetProperty("text").GetString()!);

    private static MarkdownText[] Cells(JsonElement row, int width) => Enumerable.Range(0, width)
        .Select(index => MarkdownText.Raw(index < row.GetArrayLength() ? row[index].GetString()! : "")).ToArray();
}
