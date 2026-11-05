using FlexyParse.Model;
using Microsoft.Extensions.Logging;
using Tabula;
using Tabula.Extractors;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace FlexyParse.Extraction;

public sealed class TabulaTableExtractor
{
    private readonly ILogger<TabulaTableExtractor> _logger;

    public TabulaTableExtractor(ILogger<TabulaTableExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<TablePiece> Extract(PdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
        => Extract(document, pageNumber, null, cancellationToken);

    /// <param name="tableRegions">Confirmed layout regions, or null when layout is unavailable.
    /// Ruled-table recovery is also attempted independently of these regions.</param>
    public IReadOnlyList<TablePiece> Extract(PdfDocument document, int pageNumber,
        IReadOnlyList<Box>? tableRegions, CancellationToken cancellationToken = default)
        => ExtractWithLayout(document, pageNumber,
            tableRegions?.Select(box => new PdfLayoutRegion("table", box)).ToArray(), cancellationToken);

    public IReadOnlyList<TablePiece> ExtractWithLayout(PdfDocument document, int pageNumber,
        IReadOnlyList<PdfLayoutRegion>? layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var page = ObjectExtractor.Extract(document, pageNumber);
            cancellationToken.ThrowIfCancellationRequested();
            // A real cell grid is independent evidence, even when YOLO calls an entire
            // composite figure Picture or misses a table. This is NOT stream discovery.
            List<TablePiece> tables;
            try
            {
                tables = ExtractLattice(page, cancellationToken);
                _logger.LogInformation("Lattice recovery on page {Page}: {TableCount} ruled tables.", pageNumber, tables.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogWarning(exception, "Page lattice recovery failed on page {Page}; continuing with layout candidates.", pageNumber);
                tables = [];
            }

            foreach (var detection in (layout ?? []).Where(region => region.Kind is "table" or "figure")
                .OrderBy(region => region.Kind == "figure").ThenBy(region => region.RequiresStructureValidation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var region = detection.Box;
                if (region.Area <= 0) continue;
                if (tables.Any(table => IoU(table.Box, region) > 0.8)) continue;
                try
                {
                    var area = page.GetArea(new PdfRectangle(region.Left, region.Bottom, region.Right, region.Top));
                    List<TablePiece> extracted;
                    try
                    {
                        extracted = ExtractLattice(area, cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        _logger.LogWarning(exception, "Lattice extraction failed in a confirmed table region on page {Page}; trying stream.", pageNumber);
                        extracted = [];
                    }

                    var method = "lattice";
                    if (extracted.Count == 0)
                    {
                        if (detection.Kind == "figure") continue;
                        // Do not create a broad weak stream table around an already recovered
                        // child grid; that would repeat the same data as both parent and child.
                        if (detection.RequiresStructureValidation && tables.Any(table => IsInsideRegion(table.Box, region)))
                            continue;
                        method = "stream";
                        extracted = ToPieces(new BasicExtractionAlgorithm().Extract(area), cancellationToken);
                    }

                    if (extracted.Count == 0)
                        _logger.LogInformation("No usable table structure on page {Page} in region {Left},{Bottom},{Right},{Top} after {Method}; preserving source text.",
                            pageNumber, region.Left, region.Bottom, region.Right, region.Top, method);

                    foreach (var candidate in extracted)
                    {
                        var inside = IsInsideRegion(candidate.Box, region);
                        var structure = method == "lattice" || !detection.RequiresStructureValidation || HasTabularData(candidate.Rows);
                        var accepted = inside && structure;
                        var reason = !inside ? "Outside layout region" : !structure ? "Ambiguous Picture/Table lacks repeated numeric structure" : "Accepted";
                        _logger.LogInformation("Table candidate on page {Page}: {Method}, {Left},{Bottom},{Right},{Top}, {Rows} rows; accepted: {Accepted}; {Reason}.",
                            pageNumber, method, candidate.Box.Left, candidate.Box.Bottom, candidate.Box.Right, candidate.Box.Top,
                            candidate.Rows.Count, accepted, reason);
                        if (accepted) tables.Add(candidate);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    _logger.LogWarning(exception, "Table region extraction failed on page {Page}; preserving its text.", pageNumber);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Deduplicate(tables);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "Table extraction failed on page {Page}.", pageNumber);
            return [];
        }
    }

    private static List<TablePiece> ExtractLattice(PageArea page, CancellationToken cancellationToken)
    {
        var algorithm = new SpreadsheetExtractionAlgorithm();
        var candidates = algorithm.Extract(page);
        var recovered = RotatedTableRecovery.Extract(page, candidates, cancellationToken);
        var ordinary = candidates.Where(candidate => !recovered.Any(table => IsInsideRegion(
            new Box(candidate.Left, candidate.Bottom, candidate.Right, candidate.Top), table.Box))).ToArray();
        recovered.AddRange(ToPieces(ordinary, cancellationToken, requireGridQuality: true));
        return recovered;
    }

    internal static bool IsInsideRegion(Box candidate, Box region) => candidate.Area > 0
        && candidate.IntersectionArea(region) / candidate.Area >= 0.95;

    /// <summary>Conservative extra evidence for a Table hypothesis overridden by Picture.
    /// Never promotes prose merely because it can be arranged into a rectangular grid.</summary>
    internal static bool HasTabularData(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        const int minimumRows = 3;
        const double minimumFill = 0.5;
        const double numericColumnRatio = 0.75;
        if (rows.Count < minimumRows) return false;
        var width = rows.Max(row => row.Count);
        if (width < 2 || rows.Sum(row => row.Count(cell => cell.Length > 0)) < rows.Count * width * minimumFill)
            return false;
        // The first row may be a header. At least two subsequent rows must contain short
        // numeric values in the same column. Author lists and independent prose do not.
        for (var column = 0; column < width; column++)
        {
            var numeric = rows.Skip(1).Count(row => column < row.Count && LooksNumeric(row[column]));
            if (numeric >= 2 && numeric >= (rows.Count - 1) * numericColumnRatio) return true;
        }

        return false;
    }

    private static bool LooksNumeric(string value) => value.Length is > 0 and <= 48
        && value.Any(char.IsDigit)
        && value.All(character => char.IsDigit(character) || char.IsWhiteSpace(character)
            || ".,+-−–—%±()/×eE".Contains(character, StringComparison.Ordinal));

    private static List<TablePiece> ToPieces(IReadOnlyList<Table> tables, CancellationToken cancellationToken,
        bool requireGridQuality = false)
    {
        var pieces = new List<TablePiece>(tables.Count);
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table.RowCount < 2 || table.ColumnCount < 2)
            {
                continue;
            }

            // Figure frames and screenshot separators may form a fake outer grid. Multiple
            // columns containing stacked records, including numeric data, indicate merged rows, not a
            // usable lattice result. Let a localized stream attempt recover the actual rows.
            if (requireGridQuality && table.Rows.Any(row => HasCollapsedGridRow(row.Select(cell =>
                cell is null || cell.IsPlaceholder ? "" : cell.GetText()).ToArray())))
                continue;

            var rows = new List<IReadOnlyList<string>>(table.RowCount);
            var nonEmpty = 0;
            foreach (var row in table.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cells = new string[table.ColumnCount];
                for (var column = 0; column < table.ColumnCount; column++)
                {
                    var cell = column < row.Count ? row[column] : null;
                    var text = cell is null || cell.IsPlaceholder
                        ? ""
                        : TextNormalizer.NormalizeCell(cell.GetText());
                    cells[column] = text;
                    if (text.Length > 0)
                    {
                        nonEmpty++;
                    }
                }

                rows.Add(cells);
            }

            if (nonEmpty == 0)
            {
                continue;
            }

            if (requireGridQuality && nonEmpty < (double)table.RowCount * table.ColumnCount * 0.5)
                continue;

            // Require data in multiple rows and columns, not just an oversized sparse box.
            if (rows.Count(row => row.Count(cell => cell.Length > 0) >= 2) < 2)
            {
                continue;
            }

            pieces.Add(new TablePiece
            {
                Box = new Box(table.Left, table.Bottom, table.Right, table.Top),
                Rows = rows
            });
        }

        return pieces;
    }

    internal static bool HasCollapsedGridRow(IReadOnlyList<string> cells)
    {
        var stacks = 0;
        var numericStack = false;
        foreach (var value in cells)
        {
            var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 3) continue;
            stacks++;
            numericStack |= lines.Count(LooksNumeric) >= Math.Max(3, lines.Length * 0.75);
        }

        return stacks >= 2 && numericStack;
    }

    private static List<TablePiece> Deduplicate(List<TablePiece> tables)
    {
        var kept = new List<TablePiece>(tables.Count);
        foreach (var candidate in tables)
        {
            var duplicateIndex = kept.FindIndex(existing => IoU(existing.Box, candidate.Box) > 0.8);
            if (duplicateIndex < 0)
            {
                kept.Add(candidate);
                continue;
            }

            if (NonEmptyCells(candidate) > NonEmptyCells(kept[duplicateIndex]))
            {
                kept[duplicateIndex] = candidate;
            }
        }

        return kept;
    }

    private static double IoU(Box left, Box right)
    {
        var intersection = left.IntersectionArea(right);
        var union = left.Area + right.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static int NonEmptyCells(TablePiece table)
    {
        var count = 0;
        foreach (var row in table.Rows)
        {
            foreach (var cell in row)
            {
                if (cell.Length > 0)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
