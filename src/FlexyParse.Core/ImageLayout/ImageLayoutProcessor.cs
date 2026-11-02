using FlexyParse.Options;
using FlexyParse.Model;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace FlexyParse.ImageLayout;

public sealed class ImageLayoutProcessor(
    IImageLayoutDetector detector, IOptions<FlexyParseOptions> options, ILogger<ImageLayoutProcessor> logger)
{
    private readonly ImageLayoutOptions _options = options.Value.ImageLayout;

    internal async ValueTask<ImageLayoutPlan?> PrepareAsync(byte[] bytes, CancellationToken cancellationToken,
        ImageManifestEntry? image = null, JobWorkspace? workspace = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
        {
            return null;
        }

        SKBitmap? bitmap = null;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ImageHash"] = image?.Hash, ["Pages"] = image is null ? null : string.Join(", ", image.PageNumbers),
            ["JobId"] = workspace?.JobId, ["Document"] = workspace?.SourceFileName
        });
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > _options.MaxPixels)
            {
                logger.LogWarning("Image layout skipped: unsupported image or decoded pixel limit exceeded. Using whole-image recognition.");
                return null;
            }

            bitmap = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("Could not decode the image for layout analysis.");
            var detections = await detector.DetectAsync(bitmap, cancellationToken).ConfigureAwait(false);
            var regions = ImageLayoutPlanner.Plan(detections, bitmap.Width, bitmap.Height, _options,
                out var reason, out var adjustedOverlaps, out var removedNestedRegions);
            if (regions is null)
            {
                logger.LogInformation(
                    "Model layout rejected for image {ImageHash} ({Width}x{Height}), {DetectionCount} detections: {Reason}. Whole-image recognition remains the fallback unless a whitespace layout is accepted.",
                    image?.Hash ?? "unknown", bitmap.Width, bitmap.Height, detections.Count, reason);
                if (reason is "Distinct regions overlap" or "Regions have no unambiguous whitespace-cut reading order")
                    return PrepareWhitespaceFallback();
                return null;
            }

            if (adjustedOverlaps > 0 || removedNestedRegions > 0)
            {
                logger.LogInformation(
                    "Layout conflicts resolved for image {ImageHash}: {AdjustedOverlaps} small overlaps adjusted, {RemovedNestedRegions} nested regions absorbed into their containers.",
                    image?.Hash ?? "unknown", adjustedOverlaps, removedNestedRegions);
            }

            if (regions.Count == 1)
            {
                logger.LogInformation(
                    "Layout for image {ImageHash} ({Width}x{Height}): single {RegionLabel} region; no splitting needed. Preserving the complete original image.",
                    image?.Hash ?? "unknown", bitmap.Width, bitmap.Height, regions[0].Label);
                return null;
            }

            var coverage = ImageLayoutPlanner.MeasureContentCoverage(bitmap, regions, _options, cancellationToken);
            if (coverage < _options.MinContentCoverage)
            {
                logger.LogWarning(
                    "Model layout rejected for image {ImageHash} ({Width}x{Height}), {RegionCount} regions: content coverage {Coverage:P1} is below {RequiredCoverage:P1}. Checking whitespace fallback before whole-image recognition.",
                    image?.Hash ?? "unknown", bitmap.Width, bitmap.Height, regions.Count, coverage, _options.MinContentCoverage);
                return PrepareWhitespaceFallback();
            }

            logger.LogInformation("Image layout accepted for {ImageHash} ({Width}x{Height}) with {RegionCount} regions and {Coverage:P1} content coverage.",
                image?.Hash ?? "unknown", bitmap.Width, bitmap.Height, regions.Count, coverage);
            var plan = new ImageLayoutPlan(bitmap, regions, _options.PaddingPixels);
            bitmap = null; // Ownership moves to the plan.
            return plan;

            ImageLayoutPlan? PrepareWhitespaceFallback()
            {
                if (!_options.WhitespaceFallback) return null;
                var tiles = ImageWhitespaceSplitter.Split(bitmap!, _options.MaxRegions, cancellationToken);
                if (tiles.Count < 2) return null;
                logger.LogInformation(
                    "Using full-coverage whitespace layout for image {ImageHash} ({Width}x{Height}) with {RegionCount} tiles; no source pixels are discarded.",
                    image?.Hash ?? "unknown", bitmap!.Width, bitmap.Height, tiles.Count);
                var fallback = new ImageLayoutPlan(bitmap, tiles, 0);
                bitmap = null;
                return fallback;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Local image layout detection failed. Using whole-image recognition.");
            return null;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }
}

internal sealed class ImageLayoutPlan(SKBitmap bitmap, IReadOnlyList<ImageLayoutRegion> regions, int padding) : IDisposable
{
    internal IReadOnlyList<ImageLayoutRegion> Regions => regions;

    internal byte[] Crop(ImageLayoutRegion region)
    {
        var bounds = ImageLayoutPlanner.CropBounds(region, regions, bitmap.Width, bitmap.Height, padding);
        using var crop = new SKBitmap();
        if (!bitmap.ExtractSubset(crop, bounds))
        {
            throw new InvalidOperationException("Could not crop the detected image region.");
        }

        using var encoded = crop.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode the detected image region.");
        return encoded.ToArray();
    }

    public void Dispose() => bitmap.Dispose();
}
