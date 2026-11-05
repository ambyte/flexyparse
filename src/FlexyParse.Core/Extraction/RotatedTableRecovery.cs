using System.Text;
using FlexyParse.Model;
using Tabula;
using Tabula.Extractors;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace FlexyParse.Extraction;

/// <summary>Recovers ruled grids whose rotated text cannot be read in horizontal stream order.</summary>
internal static class RotatedTableRecovery
{
    private const double RulingTolerance = 2;
    private const double MinimumGridCoverage = 0.95;
    private const int MaximumGridSlots = 100_000;

    public static List<TablePiece> Extract(PageArea page, IReadOnlyList<Table> candidates,
        CancellationToken cancellationToken)
    {
        var recovered = new List<TablePiece>();
        var letters = page.PdfPage.Letters;
        if (!letters.Any(IsVertical)) return recovered;

        foreach (var candidate in candidates.OrderByDescending(table => table.Width * table.Height))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = new Box(candidate.Left, candidate.Bottom, candidate.Right, candidate.Top);
            if (bounds.Area <= 0 || recovered.Any(table => TabulaTableExtractor.IsInsideRegion(bounds, table.Box)))
                continue;
            var content = letters.Where(letter => Contains(bounds, letter.BoundingBox.Centroid)).ToArray();
            if (!content.Any(IsVertical)) continue;

            var table = TryRecover(page, bounds, content, cancellationToken);
            if (table is not null) recovered.Add(table);
        }

        return recovered;
    }

    private static List<Cell> FindGridCells(PageArea page, Box bounds, IReadOnlyList<Letter> letters)
    {
        var rotatedLetters = letters.Where(IsVertical).ToArray();
        if (rotatedLetters.Length == 0) return [];
        // Double borders in office-generated PDFs can be 1–2 points apart. Short boxes inside
        // cells are not the primary grid; in particular they must not split wrapped headers.
        var cropped = Ruling.CropRulingsToArea(page.GetRulings(),
            new PdfRectangle(bounds.Left - RulingTolerance, bounds.Bottom - RulingTolerance,
                bounds.Right + RulingTolerance, bounds.Top + RulingTolerance));
        var primaryVertical = cropped.Where(ruling => ruling.IsVertical && ruling.Length >= bounds.Height * 0.5).ToArray();
        var headerEdges = primaryVertical.Where(ruling => ruling.Length >= bounds.Height * 0.8).ToArray();
        var rulings = cropped.Where(ruling => primaryVertical.Contains(ruling)
            || ruling.IsHorizontal && (ruling.Length >= bounds.Width * 0.5
                || headerEdges.Any(edge => Math.Abs(edge.Left - ruling.Left) <= RulingTolerance)
                    && headerEdges.Any(edge => Math.Abs(edge.Left - ruling.Right) <= RulingTolerance))).ToArray();
        var xs = Snap(rulings.SelectMany(ruling => new[] { ruling.X1, ruling.X2 }));
        var ys = Snap(rulings.SelectMany(ruling => new[] { ruling.Y1, ruling.Y2 }));
        MergeEmptyBorderStrips(xs, primaryVertical, letters);
        var snapped = rulings.Select(ruling => new Ruling(
            new PdfPoint(xs[ruling.X1], ys[ruling.Y1]), new PdfPoint(xs[ruling.X2], ys[ruling.Y2]))).ToArray();
        var horizontal = Ruling.CollapseOrientedRulings(snapped.Where(ruling => ruling.IsHorizontal).ToList());
        var vertical = Ruling.CollapseOrientedRulings(snapped.Where(ruling => ruling.IsVertical).ToList());
        var lowestRotatedText = rotatedLetters.Min(letter => letter.BoundingBox.Centroid.Y);
        var headerDivider = horizontal.Where(ruling => ruling.Top < lowestRotatedText
            && ruling.Length >= bounds.Width * 0.5).MaxBy(ruling => ruling.Top);
        if (headerDivider is not null && vertical.Count > 1)
        {
            // Some exports omit parts of the header rule over unfilled cells. Use the
            // supported divider below all rotated labels, not arbitrary body separators.
            horizontal.RemoveAll(ruling => Math.Abs(ruling.Top - headerDivider.Top) < 0.01);
            horizontal.Add(new Ruling(new PdfPoint(vertical.Min(ruling => ruling.Left), headerDivider.Top),
                new PdfPoint(vertical.Max(ruling => ruling.Left), headerDivider.Top)));
            horizontal = horizontal.OrderBy(ruling => ruling.Top).ThenBy(ruling => ruling.Left).ToList();
        }
        return SpreadsheetExtractionAlgorithm.FindCells(horizontal, vertical);
    }

    private static TablePiece? TryRecover(PageArea page, Box bounds, IReadOnlyList<Letter> letters,
        CancellationToken cancellationToken)
    {
        var cells = FindGridCells(page, bounds, letters);
        if (cells.Count < 4) return null;

        var boxes = cells.Select(cell => new Box(cell.Left, cell.Bottom, cell.Right, cell.Top)).ToArray();
        var grid = new Box(boxes.Min(box => box.Left), boxes.Min(box => box.Bottom),
            boxes.Max(box => box.Right), boxes.Max(box => box.Top));
        var coverage = boxes.Sum(box => box.Area) / grid.Area;
        if (coverage < MinimumGridCoverage || coverage > 1 / MinimumGridCoverage
            || grid.IntersectionArea(bounds) / bounds.Area < MinimumGridCoverage)
            return null;

        var columns = boxes.SelectMany(box => new[] { box.Left, box.Right }).Distinct().Order().ToArray();
        var rows = boxes.SelectMany(box => new[] { box.Top, box.Bottom }).Distinct().OrderDescending().ToArray();
        if (columns.Length < 3 || rows.Length < 3
            || (long)(columns.Length - 1) * (rows.Length - 1) > MaximumGridSlots)
            return null;

        var cellLetters = boxes.Select(_ => new List<Letter>()).ToArray();
        foreach (var letter in letters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Array.FindIndex(boxes, box => Contains(box, letter.BoundingBox.Centroid));
            // Never suppress source text if the proposed grid cannot account for its contents.
            if (index < 0 && !string.IsNullOrWhiteSpace(letter.Value)) return null;
            if (index >= 0) cellLetters[index].Add(letter);
        }

        var text = cellLetters.Select(ReadCell).ToArray();
        var result = Enumerable.Range(0, rows.Length - 1)
            .Select(_ => new string[columns.Length - 1]).ToArray();
        foreach (var row in result) Array.Fill(row, "");
        for (var index = 0; index < boxes.Length; index++)
        {
            var box = boxes[index];
            result[Array.IndexOf(rows, box.Top)][Array.IndexOf(columns, box.Left)] = text[index];
        }

        result = FlattenHeader(result, boxes, cellLetters, text, columns, rows);
        if (result.Count(row => row.Count(value => value.Length > 0) >= 2) < 2) return null;
        return new TablePiece { Box = bounds, Rows = result };
    }

    private static string[][] FlattenHeader(string[][] result, Box[] boxes, List<Letter>[] cellLetters,
        string[] text, double[] columns, double[] rows)
    {
        // A small leading header band may contain spanning group names (LOD/LOI, etc.).
        // Flatten only that band, not rotated labels appearing in the table body.
        var rotated = Enumerable.Range(0, boxes.Length).Where(index => cellLetters[index].Any(IsVertical)).ToArray();
        if (rotated.Length == 0) return result;
        var headerBottom = rotated.Min(index => boxes[index].Bottom);
        var headerRows = Array.IndexOf(rows, headerBottom);
        var firstRotatedRow = rotated.Min(index => Array.IndexOf(rows, boxes[index].Top));
        var hasLeadingGroups = Enumerable.Range(0, boxes.Length)
            .Where(index => boxes[index].Top > rows[firstRotatedRow] && text[index].Length > 0)
            .Any(index => Array.IndexOf(columns, boxes[index].Right) - Array.IndexOf(columns, boxes[index].Left) > 1);
        if (headerRows is > 0 and <= 3 && headerRows < result.Length
            && (firstRotatedRow == 0 || firstRotatedRow == 1 && hasLeadingGroups)
            && rotated.All(index => Array.IndexOf(rows, boxes[index].Top) < headerRows))
        {
            var header = new string[columns.Length - 1];
            for (var column = 0; column < header.Length; column++)
            {
                var center = (columns[column] + columns[column + 1]) / 2;
                header[column] = string.Join(": ", Enumerable.Range(0, boxes.Length)
                    .Where(index => boxes[index].Bottom >= headerBottom && boxes[index].Left <= center
                        && boxes[index].Right >= center && text[index].Length > 0)
                    .OrderByDescending(index => boxes[index].Top).Select(index => text[index]));
            }

            result = new[] { header }.Concat(result.Skip(headerRows)).ToArray();
        }

        return result;
    }

    private static Dictionary<double, double> Snap(IEnumerable<double> coordinates)
    {
        var result = new Dictionary<double, double>();
        var group = new List<double>();
        foreach (var coordinate in coordinates.Distinct().Order())
        {
            // Bound the whole cluster, rather than joining a chain of nearby real borders.
            if (group.Count > 0 && coordinate - group[0] > RulingTolerance) Flush();
            group.Add(coordinate);
        }

        Flush();
        return result;

        void Flush()
        {
            if (group.Count == 0) return;
            var average = group.Average();
            foreach (var coordinate in group) result[coordinate] = average;
            group.Clear();
        }
    }

    private static string ReadCell(List<Letter> letters)
    {
        var output = new List<string>();
        foreach (var direction in letters.GroupBy(Direction))
        {
            var (x, y) = direction.Key;
            double Along(PdfPoint point) => point.X * x + point.Y * y;
            double Across(Letter letter) => -letter.StartBaseLine.X * y + letter.StartBaseLine.Y * x;
            var lines = new List<List<Letter>>();
            foreach (var letter in direction.OrderByDescending(Across))
            {
                var line = lines.LastOrDefault();
                if (line is null || Math.Abs(Across(line[0]) - Across(letter)) > Math.Max(1, letter.PointSize * 0.25))
                    lines.Add([letter]);
                else
                    line.Add(letter);
            }

            foreach (var line in lines)
            {
                var builder = new StringBuilder();
                Letter? previous = null;
                foreach (var letter in line.OrderBy(letter => Along(letter.StartBaseLine)))
                {
                    if (previous is not null && !string.IsNullOrWhiteSpace(previous.Value)
                        && !string.IsNullOrWhiteSpace(letter.Value)
                        && Along(letter.StartBaseLine) - Along(previous.EndBaseLine) > Math.Max(0.5, letter.PointSize * 0.15))
                        builder.Append(' ');
                    builder.Append(letter.Value);
                    previous = letter;
                }

                output.Add(builder.ToString());
            }
        }

        return TextNormalizer.NormalizeCell(string.Join(" ", output));
    }

    private static void MergeEmptyBorderStrips(Dictionary<double, double> xs,
        IReadOnlyList<Ruling> vertical, IReadOnlyList<Letter> letters)
    {
        var positions = vertical.Select(ruling => xs[ruling.Left]).Distinct().Order().ToList();
        for (var index = 0; index + 1 < positions.Count; index++)
        {
            var left = positions[index];
            var right = positions[index + 1];
            if (right - left > RulingTolerance * 2 || letters.Any(letter => !string.IsNullOrWhiteSpace(letter.Value)
                && letter.BoundingBox.Centroid.X > left && letter.BoundingBox.Centroid.X < right))
                continue;

            // Inset cell outlines can survive snapping but contain no text. Prefer the
            // longer primary border; do not invent an extra, empty, 2–4 point column.
            var leftSupport = vertical.Where(ruling => xs[ruling.Left] == left).Sum(ruling => ruling.Length);
            var rightSupport = vertical.Where(ruling => xs[ruling.Left] == right).Sum(ruling => ruling.Length);
            var keep = leftSupport >= rightSupport ? left : right;
            var remove = keep == left ? right : left;
            foreach (var key in xs.Keys.ToArray())
                if (xs[key] == remove) xs[key] = keep;
            positions.Remove(remove);
            index--;
        }
    }

    private static (int X, int Y) Direction(Letter letter)
    {
        var dx = letter.EndBaseLine.X - letter.StartBaseLine.X;
        var dy = letter.EndBaseLine.Y - letter.StartBaseLine.Y;
        return Math.Abs(dy) > Math.Abs(dx) ? (0, Math.Sign(dy)) : (dx < 0 ? -1 : 1, 0);
    }

    private static bool IsVertical(Letter letter)
    {
        if (string.IsNullOrWhiteSpace(letter.Value)) return false;
        var dx = letter.EndBaseLine.X - letter.StartBaseLine.X;
        var dy = letter.EndBaseLine.Y - letter.StartBaseLine.Y;
        return Math.Abs(dy) > 0 && Math.Abs(dx) <= Math.Abs(dy) * 0.01;
    }

    private static bool Contains(Box box, PdfPoint point) => point.X >= box.Left && point.X <= box.Right
        && point.Y >= box.Bottom && point.Y <= box.Top;
}
