using FlexyParse.Options;
using SkiaSharp;

namespace FlexyParse.ImageLayout;

/// <summary>Resolves only bounded edge jitter and semantically compatible full containment.</summary>
internal static class ImageLayoutOverlapResolver
{
    internal static bool TryResolve(List<ImageLayoutRegion> regions, ImageLayoutOptions options,
        out int adjustedOverlaps, out int removedNestedRegions)
    {
        adjustedOverlaps = 0;
        removedNestedRegions = 0;

        // Resolve containment before trimming: never cut a table/figure into its internal text boxes.
        var removed = new bool[regions.Count];
        for (var i = 0; i < regions.Count; i++)
        {
            if (removed[i]) continue;
            for (var j = i + 1; j < regions.Count; j++)
            {
                if (removed[j]) continue;
                var firstContainsSecond = Contains(regions[i].Bounds, regions[j].Bounds);
                var secondContainsFirst = Contains(regions[j].Bounds, regions[i].Bounds);
                if (!firstContainsSecond && !secondContainsFirst) continue;

                var outer = firstContainsSecond ? i : j;
                var inner = firstContainsSecond ? j : i;
                if (!options.ResolveNestedRegions || !CompatibleContainer(regions[outer], regions[inner]))
                    return false;

                removed[inner] = true;
                removedNestedRegions++;
                if (inner == i) break;
            }
        }

        for (var i = regions.Count - 1; i >= 0; i--)
            if (removed[i]) regions.RemoveAt(i);

        // Keep original bounds for cumulative budgets: several small cuts must not erode a region unboundedly.
        var original = regions.Select(region => region.Bounds).ToArray();
        for (var i = 0; i < regions.Count; i++)
        {
            for (var j = i + 1; j < regions.Count; j++)
            {
                var first = regions[i].Bounds;
                var second = regions[j].Bounds;
                var overlapWidth = Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left);
                var overlapHeight = Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top);
                if (overlapWidth <= 0 || overlapHeight <= 0) continue;

                var horizontal = TrySplit(first, second, original[i], original[j], true, options,
                    out var horizontalFirst, out var horizontalSecond);
                var vertical = TrySplit(first, second, original[i], original[j], false, options,
                    out var verticalFirst, out var verticalSecond);
                if (!horizontal && !vertical) return false;

                // Prefer the smaller proportional intrusion; horizontal text bands win ties.
                var useHorizontal = horizontal && (!vertical
                    || overlapWidth / (double)Math.Min(original[i].Width, original[j].Width)
                    < overlapHeight / (double)Math.Min(original[i].Height, original[j].Height));
                regions[i] = regions[i] with { Bounds = useHorizontal ? horizontalFirst : verticalFirst };
                regions[j] = regions[j] with { Bounds = useHorizontal ? horizontalSecond : verticalSecond };
                adjustedOverlaps++;
            }
        }

        return true;
    }

    private static bool CompatibleContainer(ImageLayoutRegion outer, ImageLayoutRegion inner)
    {
        var outerKind = ImageLayoutPlanner.RegionKind(outer.Label);
        var innerKind = ImageLayoutPlanner.RegionKind(inner.Label);
        return outerKind == innerKind
            || (outerKind is "table" or "figure" or "formula" && innerKind == "text")
            || (outerKind is "table" or "figure" && innerKind == "formula");
    }

    private static bool Contains(SKRectI outer, SKRectI inner) =>
        outer.Left <= inner.Left && outer.Top <= inner.Top && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    private static bool TrySplit(SKRectI first, SKRectI second, SKRectI originalFirst, SKRectI originalSecond,
        bool horizontal, ImageLayoutOptions options, out SKRectI adjustedFirst, out SKRectI adjustedSecond)
    {
        adjustedFirst = first;
        adjustedSecond = second;
        var firstStart = horizontal ? first.Left : first.Top;
        var firstEnd = horizontal ? first.Right : first.Bottom;
        var secondStart = horizontal ? second.Left : second.Top;
        var secondEnd = horizontal ? second.Right : second.Bottom;
        var firstBeforeSecond = firstStart < secondStart && firstEnd < secondEnd;
        var secondBeforeFirst = secondStart < firstStart && secondEnd < firstEnd;
        // Nested projections are not neighboring blocks along this axis.
        if (!firstBeforeSecond && !secondBeforeFirst) return false;

        var start = Math.Max(firstStart, secondStart);
        var end = Math.Min(firstEnd, secondEnd);
        var depth = end - start;
        var firstSize = horizontal ? originalFirst.Width : originalFirst.Height;
        var secondSize = horizontal ? originalSecond.Width : originalSecond.Height;
        if (depth > options.MaxOverlapPixels || depth > Math.Min(firstSize, secondSize) * options.MaxOverlapRatio)
            return false;

        var boundary = start + depth / 2;
        if (horizontal)
        {
            adjustedFirst = firstBeforeSecond ? new(first.Left, first.Top, boundary, first.Bottom)
                : new(boundary, first.Top, first.Right, first.Bottom);
            adjustedSecond = firstBeforeSecond ? new(boundary, second.Top, second.Right, second.Bottom)
                : new(second.Left, second.Top, boundary, second.Bottom);
        }
        else
        {
            adjustedFirst = firstBeforeSecond ? new(first.Left, first.Top, first.Right, boundary)
                : new(first.Left, boundary, first.Right, first.Bottom);
            adjustedSecond = firstBeforeSecond ? new(second.Left, boundary, second.Right, second.Bottom)
                : new(second.Left, second.Top, second.Right, boundary);
        }

        return WithinBudget(originalFirst, adjustedFirst, options) && WithinBudget(originalSecond, adjustedSecond, options);
    }

    private static bool WithinBudget(SKRectI original, SKRectI adjusted, ImageLayoutOptions options)
    {
        if (adjusted.Width <= 0 || adjusted.Height <= 0) return false;
        var area = (double)original.Width * original.Height;
        var lostArea = area - (double)adjusted.Width * adjusted.Height;
        return lostArea <= area * options.MaxOverlapRatio
            && adjusted.Left - original.Left <= options.MaxOverlapPixels
            && adjusted.Top - original.Top <= options.MaxOverlapPixels
            && original.Right - adjusted.Right <= options.MaxOverlapPixels
            && original.Bottom - adjusted.Bottom <= options.MaxOverlapPixels;
    }
}
