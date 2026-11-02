using FlexyParse.Options;
using SkiaSharp;

namespace FlexyParse.ImageLayout;

internal static class ImageLayoutPlanner
{
    private const double SpanningBlockWidthRatio = 0.7;

    internal static string? RegionKind(string label) => label.Trim().ToLowerInvariant()
        .Replace("_", "").Replace("-", "").Replace(" ", "") switch
    {
        "text" or "plaintext" or "title" or "sectionheader" or "caption" or "footnote"
            or "pageheader" or "pagefooter" or "listitem" => "text",
        "formula" or "isolateformula" => "formula",
        "table" => "table",
        "picture" or "figure" => "figure",
        _ => null
    };

    internal static IReadOnlyList<ImageLayoutRegion>? Plan(
        IReadOnlyList<ImageLayoutRegion> detections, int width, int height, ImageLayoutOptions options)
        => Plan(detections, width, height, options, out _);

    internal static IReadOnlyList<ImageLayoutRegion>? Plan(
        IReadOnlyList<ImageLayoutRegion> detections, int width, int height, ImageLayoutOptions options,
        out string? rejectionReason)
        => Plan(detections, width, height, options, out rejectionReason, out _, out _);

    internal static IReadOnlyList<ImageLayoutRegion>? Plan(
        IReadOnlyList<ImageLayoutRegion> detections, int width, int height, ImageLayoutOptions options,
        out string? rejectionReason, out int adjustedOverlaps, out int removedNestedRegions)
    {
        rejectionReason = null;
        adjustedOverlaps = 0;
        removedNestedRegions = 0;
        if (detections.Count == 0 || detections.Count > options.MaxRegions || width <= 0 || height <= 0)
        {
            rejectionReason = detections.Count == 0 ? "No document regions detected"
                : detections.Count > options.MaxRegions ? "Region count exceeds the configured limit" : "Invalid image dimensions";
            return null;
        }

        var regions = new List<ImageLayoutRegion>();
        foreach (var region in detections.OrderByDescending(region => region.Confidence)
            .ThenByDescending(region => Area(region.Bounds))
            .ThenBy(region => region.Bounds.Top).ThenBy(region => region.Bounds.Left)
            .ThenBy(region => region.Bounds.Bottom).ThenBy(region => region.Bounds.Right)
            .ThenBy(region => region.Label, StringComparer.Ordinal))
        {
            var box = region.Bounds;
            var edgeTolerance = Math.Max(2, (int)Math.Ceiling(Math.Max(width, height) * 0.02));
            if (!double.IsFinite(region.Confidence) || region.Confidence < options.Confidence
                || region.Confidence > 1 || RegionKind(region.Label) is null
                || box.Left < -edgeTolerance || box.Top < -edgeTolerance
                || box.Right > width + edgeTolerance || box.Bottom > height + edgeTolerance
                || box.Right <= box.Left || box.Bottom <= box.Top)
            {
                rejectionReason = "Invalid region label, confidence or bounds";
                return null;
            }

            box = new SKRectI(Math.Clamp(box.Left, 0, width), Math.Clamp(box.Top, 0, height),
                Math.Clamp(box.Right, 0, width), Math.Clamp(box.Bottom, 0, height));
            if (box.Width <= 0 || box.Height <= 0)
            {
                rejectionReason = "Region does not intersect the image";
                return null;
            }

            // YOLOv10 may emit the same box with competing class scores. Keep the highest-confidence
            // interpretation regardless of label; it is still one physical region, not two overlapping crops.
            var duplicate = false;
            foreach (var existing in regions)
            {
                var intersection = IntersectionArea(box, existing.Bounds);
                if (intersection == 0)
                {
                    continue;
                }

                var union = Area(box) + Area(existing.Bounds) - intersection;
                if (intersection / union >= 0.9)
                {
                    duplicate = true;
                    break;
                }

            }

            if (!duplicate)
            {
                regions.Add(region with { Bounds = box });
            }
        }

        if (!ImageLayoutOverlapResolver.TryResolve(regions, options, out adjustedOverlaps, out removedNestedRegions))
        {
            rejectionReason = "Distinct regions overlap";
            return null;
        }

        var ordered = new List<ImageLayoutRegion>(regions.Count);
        if (Order(regions, ordered)) return ordered;
        rejectionReason = "Regions have no unambiguous whitespace-cut reading order";
        return null;
    }

    // Recursive whitespace cuts: columns first, then horizontal bands around spanning blocks.
    // A global top/left sort would incorrectly interleave lines from independent columns.
    private static bool Order(List<ImageLayoutRegion> regions, List<ImageLayoutRegion> output)
    {
        if (regions.Count == 1)
        {
            output.Add(regions[0]);
            return true;
        }

        foreach (var vertical in new[] { true, false })
        {
            var sorted = regions.OrderBy(region => vertical ? region.Bounds.Left : region.Bounds.Top).ToList();
            var end = vertical ? sorted[0].Bounds.Right : sorted[0].Bounds.Bottom;
            var split = -1;
            var largestGap = -1;
            var spanningBoundarySelected = false;
            var totalWidth = regions.Max(region => region.Bounds.Right) - regions.Min(region => region.Bounds.Left);
            for (var i = 1; i < sorted.Count; i++)
            {
                var start = vertical ? sorted[i].Bounds.Left : sorted[i].Bounds.Top;
                var gap = start - end;
                // With a spanning heading/table, a paragraph gap can otherwise split both columns
                // into rows before the algorithm ever gets a chance to find their vertical gutter.
                var spanningBoundary = !vertical && gap >= 0 && regions.Any(region =>
                    region.Bounds.Width >= totalWidth * SpanningBlockWidthRatio
                    && (region.Bounds.Bottom == end || region.Bounds.Top == start));
                // Half-open crop rectangles can share an edge without sharing any pixels.
                if (gap >= 0 && ((spanningBoundary && !spanningBoundarySelected)
                    || (spanningBoundary == spanningBoundarySelected && gap > largestGap)))
                {
                    largestGap = gap;
                    split = i;
                    spanningBoundarySelected = spanningBoundary;
                }

                end = Math.Max(end, vertical ? sorted[i].Bounds.Right : sorted[i].Bounds.Bottom);
            }

            if (split > 0)
            {
                return Order(sorted.GetRange(0, split), output)
                    && Order(sorted.GetRange(split, sorted.Count - split), output);
            }
        }

        return false;
    }

    internal static SKRectI CropBounds(ImageLayoutRegion region, IReadOnlyList<ImageLayoutRegion> regions,
        int width, int height, int padding)
    {
        var box = region.Bounds;
        var padded = new SKRectI(Math.Max(0, box.Left - padding), Math.Max(0, box.Top - padding),
            Math.Min(width, box.Right + padding), Math.Min(height, box.Bottom + padding));
        // Never extend a crop into a neighboring detected block, which would duplicate its text.
        return regions.Any(other => other != region && IntersectionArea(padded, other.Bounds) > 0) ? box : padded;
    }

    internal static bool HasContentCoverage(SKBitmap bitmap, IReadOnlyList<ImageLayoutRegion> regions,
        ImageLayoutOptions options, CancellationToken cancellationToken)
        => MeasureContentCoverage(bitmap, regions, options, cancellationToken) >= options.MinContentCoverage;

    internal static double MeasureContentCoverage(SKBitmap bitmap, IReadOnlyList<ImageLayoutRegion> regions,
        ImageLayoutOptions options, CancellationToken cancellationToken)
    {
        var boxes = regions.Select(region => CropBounds(region, regions, bitmap.Width, bitmap.Height, options.PaddingPixels)).ToArray();
        var stride = Math.Max(1, (int)Math.Sqrt((long)bitmap.Width * bitmap.Height / 250_000d));
        long content = 0;
        long covered = 0;
        for (var y = 0; y < bitmap.Height; y += stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < bitmap.Width; x += stride)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 16 || (pixel.Red >= 230 && pixel.Green >= 230 && pixel.Blue >= 230))
                {
                    continue;
                }

                content++;
                foreach (var box in boxes)
                {
                    if (x >= box.Left && x < box.Right && y >= box.Top && y < box.Bottom)
                    {
                        covered++;
                        break;
                    }
                }
            }
        }

        return content > 0 ? covered / (double)content : 0;
    }

    private static double Area(SKRectI box) => (double)box.Width * box.Height;

    private static double IntersectionArea(SKRectI left, SKRectI right) =>
        (double)Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left))
        * Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
}
