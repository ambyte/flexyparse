using FlexyParse.Model;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace FlexyParse.Extraction;

public sealed class PdfTextExtractor
{
    private readonly ILogger<PdfTextExtractor> _logger;

    public PdfTextExtractor(ILogger<PdfTextExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<TextPiece> Extract(Page page, CancellationToken cancellationToken = default)
        => Extract(page, [], null, cancellationToken);

    public IReadOnlyList<TextPiece> Extract(Page page, IReadOnlyList<Box> acceptedTables,
        IReadOnlyList<PdfLayoutRegion>? layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        if (page.Letters.Count == 0)
        {
            return [];
        }

        List<Word>? words = null;
        try
        {
            words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters)
                .Where(word => !acceptedTables.Any(table => CoversWord(table, word))).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            if (words.Count == 0)
            {
                return acceptedTables.Count == 0 ? Fallback(page) : [];
            }

            var pieces = new List<TextPiece>();
            // Assign each word once. Detector omissions remain in the remainder instead of
            // disappearing; rejected table regions are also read as ordinary PDF text.
            var remaining = words.ToList();
            foreach (var region in layout ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (region.Kind is not ("text" or "formula")) continue;
                var group = remaining.Where(word => CoversWord(region.Box, word)).ToList();
                if (group.Count == 0) continue;
                var assigned = group.ToHashSet();
                remaining.RemoveAll(assigned.Contains);
                AppendPieces(group, pieces, cancellationToken);
            }

            AppendPieces(remaining, pieces, cancellationToken);
            return pieces.Count == 0 ? WordFallback(words) : pieces;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "Layout analysis failed on page {Page}. Raw page text is used instead.", page.Number);
            if (words is not null) return WordFallback(words);
            return Fallback(page, acceptedTables);
        }
    }

    private static bool CoversWord(Box box, Word word)
    {
        var bounds = word.BoundingBox;
        var centerX = (bounds.Left + bounds.Right) / 2;
        var centerY = (bounds.Bottom + bounds.Top) / 2;
        return centerX >= box.Left && centerX <= box.Right && centerY >= box.Bottom && centerY <= box.Top;
    }

    private static void AppendPieces(List<Word> words, List<TextPiece> pieces, CancellationToken cancellationToken)
    {
        if (words.Count == 0) return;
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        foreach (var block in UnsupervisedReadingOrderDetector.Instance.Get(blocks))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = TextNormalizer.NormalizeBlock(block.Text);
            if (text.Length == 0) continue;
            var box = block.BoundingBox;
            pieces.Add(new TextPiece
            {
                Box = new Box(box.Left, box.Bottom, box.Right, box.Top),
                Text = text,
                AverageFontSize = AverageFontSize(block),
                LineCount = Math.Max(1, block.TextLines.Count)
            });
        }
    }

    private static IReadOnlyList<TextPiece> WordFallback(List<Word> words) => words.Select(word => new TextPiece
    {
        Box = new Box(word.BoundingBox.Left, word.BoundingBox.Bottom, word.BoundingBox.Right, word.BoundingBox.Top),
        Text = word.Text,
        AverageFontSize = word.Letters.Count == 0 ? 0 : word.Letters.Average(letter => letter.FontSize),
        LineCount = 1
    }).ToArray();

    private static IReadOnlyList<TextPiece> Fallback(Page page, IReadOnlyList<Box>? acceptedTables = null)
    {
        // Even a segmentation failure must not restore text already emitted by a table.
        var text = TextNormalizer.NormalizeBlock(acceptedTables is null || acceptedTables.Count == 0 ? page.Text
            : string.Concat(page.Letters.Where(letter => !acceptedTables.Any(table =>
                table.Left <= letter.BoundingBox.Centroid.X && table.Right >= letter.BoundingBox.Centroid.X
                && table.Bottom <= letter.BoundingBox.Centroid.Y && table.Top >= letter.BoundingBox.Centroid.Y))
                .Select(letter => letter.Value)));
        if (text.Length == 0)
        {
            return [];
        }

        return
        [
            new TextPiece
            {
                Box = new Box(0, 0, page.Width, page.Height),
                Text = text,
                AverageFontSize = 0,
                LineCount = text.Count(character => character == '\n') + 1
            }
        ];
    }

    private static double AverageFontSize(UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock block)
    {
        double total = 0;
        var count = 0;
        foreach (var line in block.TextLines)
        {
            foreach (var word in line.Words)
            {
                foreach (var letter in word.Letters)
                {
                    total += letter.FontSize;
                    count++;
                }
            }
        }

        return count == 0 ? 0 : total / count;
    }
}
