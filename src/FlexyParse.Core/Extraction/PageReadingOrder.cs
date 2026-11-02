using FlexyParse.Model;

namespace FlexyParse.Extraction;

/// <summary>Recursive whitespace cuts preserve independent columns and spanning page bands.</summary>
internal static class PageReadingOrder
{
    private const double SpanningWidthRatio = 0.7;
    private const int MaxCutDepth = 64;

    internal static IReadOnlyList<T> Order<T>(IReadOnlyList<T> items, Func<T, Box> bounds)
    {
        var result = new List<T>(items.Count);
        Append(items.ToList(), bounds, result, 0);
        return result;
    }

    private static void Append<T>(List<T> items, Func<T, Box> bounds, List<T> output, int depth)
    {
        if (items.Count <= 1)
        {
            output.AddRange(items);
            return;
        }

        foreach (var vertical in depth < MaxCutDepth ? new[] { true, false } : [])
        {
            var sorted = items.OrderBy(item => vertical ? bounds(item).Left : -bounds(item).Top).ToList();
            var end = vertical ? bounds(sorted[0]).Right : -bounds(sorted[0]).Bottom;
            var totalWidth = items.Max(item => bounds(item).Right) - items.Min(item => bounds(item).Left);
            var largestGap = 0d;
            var split = -1;
            var spanningSelected = false;
            for (var index = 1; index < sorted.Count; index++)
            {
                var box = bounds(sorted[index]);
                var start = vertical ? box.Left : -box.Top;
                var gap = start - end;
                var spanningBoundary = !vertical && gap > 0 && items.Any(item =>
                    bounds(item).Width >= totalWidth * SpanningWidthRatio
                    && (-bounds(item).Bottom == end || -bounds(item).Top == start));
                if (gap > 0 && ((spanningBoundary && !spanningSelected)
                    || (spanningBoundary == spanningSelected && gap > largestGap)))
                {
                    largestGap = gap;
                    split = index;
                    spanningSelected = spanningBoundary;
                }

                end = Math.Max(end, vertical ? box.Right : -box.Bottom);
            }

            if (split > 0)
            {
                Append(sorted.GetRange(0, split), bounds, output, depth + 1);
                Append(sorted.GetRange(split, sorted.Count - split), bounds, output, depth + 1);
                return;
            }
        }

        // Overlapping/ambiguous blocks retain a deterministic geometric fallback.
        output.AddRange(items.OrderByDescending(item => bounds(item).Top).ThenBy(item => bounds(item).Left));
    }
}
