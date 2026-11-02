using SkiaSharp;

namespace FlexyParse.ImageLayout;

/// <summary>Partitions every source pixel using wide, uniform gutters, independent of document classes.</summary>
public static class ImageWhitespaceSplitter
{
    private const int MinimumSide = 64;
    private const int MaximumDepth = 4;
    private const int ColorTolerance = 12;

    /// <summary>Returns full-coverage, nonoverlapping tiles in column-first reading order.
    /// A single tile means no safe cut was found. Uniform dark backgrounds are also whitespace.</summary>
    public static IReadOnlyList<ImageLayoutRegion> Split(SKBitmap bitmap, int maxRegions, CancellationToken cancellationToken,
        int minimumSide = MinimumSide, int minimumGapPixels = 8, double maxInkRatio = 0)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRegions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSide, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumGapPixels, 1);
        if (!double.IsFinite(maxInkRatio) || maxInkRatio is < 0 or > 0.05)
            throw new ArgumentOutOfRangeException(nameof(maxInkRatio));
        cancellationToken.ThrowIfCancellationRequested();
        var output = new List<ImageLayoutRegion>();
        Partition(new SKRectI(0, 0, bitmap.Width, bitmap.Height), Math.Min(maxRegions, 16), 0);
        return output;

        void Partition(SKRectI bounds, int budget, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget > 1 && depth < MaximumDepth)
            {
                // Never invent a row-major order across independent columns.
                foreach (var vertical in new[] { true, false })
                {
                    var cut = FindCut(bitmap, bounds, vertical, minimumSide, minimumGapPixels, maxInkRatio, cancellationToken);
                    if (cut < 0) continue;
                    var first = vertical ? new SKRectI(bounds.Left, bounds.Top, cut, bounds.Bottom)
                        : new SKRectI(bounds.Left, bounds.Top, bounds.Right, cut);
                    var second = vertical ? new SKRectI(cut, bounds.Top, bounds.Right, bounds.Bottom)
                        : new SKRectI(bounds.Left, cut, bounds.Right, bounds.Bottom);
                    Partition(first, budget / 2, depth + 1);
                    Partition(second, budget - budget / 2, depth + 1);
                    return;
                }
            }

            // No semantic claim: the OCR provider must still recognize tables/figures within a tile.
            output.Add(new ImageLayoutRegion("Unclassified", bounds, 1));
        }
    }

    private static int FindCut(SKBitmap bitmap, SKRectI bounds, bool vertical, int minimumSide, int minimumGapPixels,
        double maxInkRatio, CancellationToken token)
    {
        var start = vertical ? bounds.Left : bounds.Top;
        var end = vertical ? bounds.Right : bounds.Bottom;
        if (end - start < (long)minimumSide * 2) return -1;
        // Small two-page screenshots may have 9–10px margins on either side of a separator rule.
        // Requiring 2% rejected both margins even though each is completely uniform.
        var minimumGap = Math.Max(minimumGapPixels, (end - start) / 100);
        var runStart = -1;
        var bestCut = -1;
        var bestWidth = 0;
        var hasContentBefore = false;
        for (var position = start; position < end; position++)
        {
            token.ThrowIfCancellationRequested();
            if (IsUniformLine(bitmap, bounds, position, vertical, maxInkRatio))
            {
                if (runStart < 0) runStart = position;
                continue;
            }

            if (hasContentBefore && runStart >= 0)
            {
                var width = position - runStart;
                var cut = runStart + width / 2;
                if (width >= minimumGap && width > bestWidth
                    && cut - start >= minimumSide && end - cut >= minimumSide)
                {
                    bestWidth = width;
                    bestCut = cut;
                }
            }
            hasContentBefore = true;
            runStart = -1;
        }

        // Leading/trailing margins and entirely blank images are never split.
        return bestCut;
    }

    private static bool IsUniformLine(SKBitmap bitmap, SKRectI bounds, int position, bool vertical, double maxInkRatio)
    {
        var start = vertical ? bounds.Top : bounds.Left;
        var end = vertical ? bounds.Bottom : bounds.Right;
        var minimumRed = 255;
        var minimumGreen = 255;
        var minimumBlue = 255;
        var maximumRed = 0;
        var maximumGreen = 0;
        var maximumBlue = 0;
        var ink = 0;
        var allowedInk = (int)((end - start) * maxInkRatio);
        for (var index = start; index < end; index++)
        {
            var pixel = vertical ? bitmap.GetPixel(position, index) : bitmap.GetPixel(index, position);
            // Composite transparency onto white, as the recognizers do.
            var red = (pixel.Red * pixel.Alpha + 255 * (255 - pixel.Alpha)) / 255;
            var green = (pixel.Green * pixel.Alpha + 255 * (255 - pixel.Alpha)) / 255;
            var blue = (pixel.Blue * pixel.Alpha + 255 * (255 - pixel.Alpha)) / 255;
            if (red < 230 || green < 230 || blue < 230) ink++;
            minimumRed = Math.Min(minimumRed, red);
            minimumGreen = Math.Min(minimumGreen, green);
            minimumBlue = Math.Min(minimumBlue, blue);
            maximumRed = Math.Max(maximumRed, red);
            maximumGreen = Math.Max(maximumGreen, green);
            maximumBlue = Math.Max(maximumBlue, blue);
            var varied = maximumRed - minimumRed > ColorTolerance || maximumGreen - minimumGreen > ColorTolerance
                || maximumBlue - minimumBlue > ColorTolerance;
            if (varied && (maxInkRatio == 0 || ink > allowedInk)) return false;
        }
        return true;
    }
}
