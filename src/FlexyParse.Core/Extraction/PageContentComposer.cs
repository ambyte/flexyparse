using FlexyParse.Model;

namespace FlexyParse.Extraction;

/// <summary>
/// Merges text, tables and images into reading order and drops text that only repeats a table.
/// </summary>
public static class PageContentComposer
{
    public const double TableOverlapRatio = 0.5;

    public static IReadOnlyList<ContentBlock> Compose(
        IReadOnlyList<TextPiece> text,
        IReadOnlyList<TablePiece> tables,
        IReadOnlyList<ImagePiece> images,
        bool textExcludesTables = false)
    {
        var tableBoxes = new Box[tables.Count];
        for (var index = 0; index < tables.Count; index++)
        {
            tableBoxes[index] = tables[index].Box;
        }

        var medianFontSize = Median(text.Select(piece => piece.AverageFontSize).Where(size => size > 0));
        var positioned = new List<Positioned>(text.Count + tables.Count + images.Count);

        foreach (var piece in text)
        {
            if (piece.Text.Length == 0 || (!textExcludesTables && OverlapsTable(piece.Box, tableBoxes)))
            {
                continue;
            }

            ContentBlock block = IsHeading(piece, medianFontSize)
                ? new HeadingBlock
                {
                    Level = HeadingLevel(piece.AverageFontSize, medianFontSize),
                    Text = piece.Text,
                    Top = piece.Box.Top,
                    Left = piece.Box.Left
                }
                : new ParagraphBlock
                {
                    Text = piece.Text,
                    Top = piece.Box.Top,
                    Left = piece.Box.Left
                };
            positioned.Add(new Positioned(piece.Box, block));
        }

        foreach (var table in tables)
        {
            if (table.Rows.Count == 0)
            {
                continue;
            }

            positioned.Add(new Positioned(
                table.Box,
                new TableBlock
                {
                    Rows = table.Rows,
                    Top = table.Box.Top,
                    Left = table.Box.Left
                }));
        }

        foreach (var image in images)
        {
            positioned.Add(new Positioned(
                image.Box,
                new ImageBlock
                {
                    Id = image.Id,
                    ContentHash = image.ContentHash,
                    PixelWidth = image.PixelWidth,
                    PixelHeight = image.PixelHeight,
                    Note = image.Note,
                    Top = image.Box.Top,
                    Left = image.Box.Left
                }));
        }

        var ordered = PageReadingOrder.Order(positioned, item => item.Box);

        var blocks = new ContentBlock[positioned.Count];
        for (var index = 0; index < positioned.Count; index++)
        {
            blocks[index] = ordered[index].Block;
        }

        return blocks;
    }

    private static bool OverlapsTable(Box box, IReadOnlyList<Box> tables)
    {
        if (box.Area <= 0)
        {
            return false;
        }

        foreach (var table in tables)
        {
            if (box.IntersectionArea(table) / box.Area >= TableOverlapRatio)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHeading(TextPiece piece, double medianFontSize)
    {
        if (medianFontSize <= 0 || piece.LineCount > 2 || piece.Text.Length > 160)
        {
            return false;
        }

        if (piece.Text.Length > 80 && piece.Text.EndsWith('.'))
        {
            return false;
        }

        return piece.AverageFontSize >= medianFontSize * 1.15;
    }

    private static int HeadingLevel(double fontSize, double medianFontSize)
    {
        var ratio = fontSize / medianFontSize;
        if (ratio >= 1.8)
        {
            return 1;
        }

        return ratio >= 1.45 ? 2 : 3;
    }

    private static double Median(IEnumerable<double> values)
    {
        var list = values.ToArray();
        if (list.Length == 0)
        {
            return 0;
        }

        Array.Sort(list);
        var middle = list.Length / 2;
        return list.Length % 2 == 1
            ? list[middle]
            : (list[middle - 1] + list[middle]) / 2;
    }

    private readonly record struct Positioned(Box Box, ContentBlock Block);
}
