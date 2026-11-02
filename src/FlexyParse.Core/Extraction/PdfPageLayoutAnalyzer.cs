using System.Globalization;
using System.Text.Json;
using FlexyParse.ImageLayout;
using FlexyParse.Model;
using FlexyParse.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace FlexyParse.Extraction;

/// <summary>Uses a bounded page render for classification only; text still comes from PdfPig.</summary>
public sealed class PdfPageLayoutAnalyzer(
    PdfImageFallbackRenderer renderer,
    IImageLayoutDetector detector,
    IOptions<FlexyParseOptions> options,
    ILogger<PdfPageLayoutAnalyzer> logger)
{
    public async ValueTask<IReadOnlyList<PdfLayoutRegion>?> AnalyzeAsync(
        string sourcePath, int pageNumber, double width, double height, CancellationToken cancellationToken,
        string? diagnosticsDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value.ImageLayout;
        if (!settings.Enabled) return null;

        try
        {
            using var bitmap = renderer.RenderBitmap(sourcePath, pageNumber, width, height,
                new Box(0, 0, width, height), cancellationToken, settings.MaxPixels);
            if ((long)bitmap.Width * bitmap.Height > settings.MaxPixels)
            {
                logger.LogWarning("Page layout skipped on page {Page}: render exceeds the layout pixel limit. Using lattice-only extraction.", pageNumber);
                return null;
            }

            var detections = await detector.DetectAsync(bitmap, cancellationToken).ConfigureAwait(false);
            if (detections.Count > settings.MaxRegions)
                throw new InvalidDataException("Page layout region limit exceeded.");
            var regions = MapRegions(detections, bitmap.Width, bitmap.Height, width, height, settings.Confidence).ToList();
            var figures = regions.Where(region => region.Kind == "figure").OrderByDescending(region => region.Box.Area)
                .Take(settings.MaxFigurePasses).ToArray();
            for (var index = 0; index < figures.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var figure = figures[index];
                try
                {
                    // A composite screenshot may be Picture at page scale while its individual
                    // tables become visible when the detector sees just the figure. One level only.
                    using var crop = renderer.RenderBitmap(sourcePath, pageNumber, width, height, figure.Box,
                        cancellationToken, settings.MaxPixels);
                    var nested = await detector.DetectAsync(crop, cancellationToken).ConfigureAwait(false);
                    if (nested.Count > settings.MaxRegions)
                        throw new InvalidDataException("Figure layout region limit exceeded.");
                    var mapped = MapRegions(nested, crop.Width, crop.Height, figure.Box.Width, figure.Box.Height,
                        settings.Confidence).Select(region => region with
                        {
                            Box = new Box(region.Box.Left + figure.Box.Left, region.Box.Bottom + figure.Box.Bottom,
                                region.Box.Right + figure.Box.Left, region.Box.Top + figure.Box.Bottom)
                        }).ToArray();
                    foreach (var table in mapped.Where(region => region.Kind == "table"))
                    {
                        if (regions.Count >= settings.MaxRegions) break;
                        regions.Add(table);
                    }

                    logger.LogInformation("Figure layout on page {Page}, figure {Figure}: {TableCount} nested table candidates.",
                        pageNumber, index + 1, mapped.Count(region => region.Kind == "table"));
                    if (diagnosticsDirectory is not null)
                        await SaveDiagnosticsAsync(crop, nested, mapped, figure.Box, diagnosticsDirectory, pageNumber,
                            cancellationToken, "-figure-" + (index + 1).ToString("D2", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger.LogWarning(exception, "Figure layout failed on page {Page}, figure {Figure}; keeping page candidates and lattice recovery.",
                        pageNumber, index + 1);
                }
            }

            if (diagnosticsDirectory is not null)
                await SaveDiagnosticsAsync(bitmap, detections, regions, new Box(0, 0, width, height),
                    diagnosticsDirectory, pageNumber, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Page layout on page {Page}: {RegionCount} regions, {TableCount} table candidates, {WeakTableCount} requiring structural validation.",
                pageNumber, regions.Count, regions.Count(region => region.Kind == "table"),
                regions.Count(region => region.Kind == "table" && region.RequiresStructureValidation));
            return regions;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Page layout failed on page {Page}. Using lattice-only extraction; unconfirmed text remains text.", pageNumber);
            return null;
        }
    }

    private async Task SaveDiagnosticsAsync(SKBitmap bitmap, IReadOnlyList<ImageLayoutRegion> detections,
        IReadOnlyList<PdfLayoutRegion> regions, Box viewport, string directory, int pageNumber,
        CancellationToken cancellationToken, string suffix = "")
    {
        try
        {
            var prefix = Path.Combine(directory, "page-layout-" + pageNumber.ToString("D4", CultureInfo.InvariantCulture) + suffix);
            await using (var stream = File.Create(prefix + ".json"))
            {
                await JsonSerializer.SerializeAsync(stream, new
                {
                    bitmap.Width,
                    bitmap.Height,
                    PdfViewport = viewport,
                    ResolvedRegions = regions,
                    Detections = detections.Select(region => new
                    {
                        region.Label, Confidence = double.IsFinite(region.Confidence) ? (double?)region.Confidence : null,
                        region.Bounds.Left, region.Bounds.Top, region.Bounds.Right, region.Bounds.Bottom
                    })
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            using var overlay = bitmap.Copy();
            using (var canvas = new SKCanvas(overlay))
            using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true })
            {
                foreach (var region in detections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    paint.Color = ImageLayoutPlanner.RegionKind(region.Label) == "table" ? SKColors.Red : SKColors.Blue;
                    canvas.DrawRect(region.Bounds, paint);
                }

                foreach (var region in regions.Where(region => region.Kind == "table"))
                {
                    paint.Color = region.RequiresStructureValidation ? SKColors.Orange : SKColors.Green;
                    canvas.DrawRect(new SKRect(
                        (float)((region.Box.Left - viewport.Left) / viewport.Width * bitmap.Width),
                        (float)((viewport.Top - region.Box.Top) / viewport.Height * bitmap.Height),
                        (float)((region.Box.Right - viewport.Left) / viewport.Width * bitmap.Width),
                        (float)((viewport.Top - region.Box.Bottom) / viewport.Height * bitmap.Height)), paint);
                }
            }

            using var encoded = overlay.Encode(SKEncodedImageFormat.Png, 100);
            await using var imageStream = File.Create(prefix + ".png");
            using var encodedStream = encoded.AsStream();
            await encodedStream.CopyToAsync(imageStream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not save page layout diagnostics on page {Page}.", pageNumber);
        }
    }

    internal static IReadOnlyList<PdfLayoutRegion> MapRegions(IReadOnlyList<ImageLayoutRegion> detections,
        int pixelWidth, int pixelHeight, double width, double height, double confidence)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));

        var kept = new List<ImageLayoutRegion>();
        var alternatives = new List<ImageLayoutRegion>();
        foreach (var detection in detections.OrderByDescending(region => region.Confidence))
        {
            if (!double.IsFinite(detection.Confidence) || detection.Confidence < confidence || detection.Confidence > 1
                || ImageLayoutPlanner.RegionKind(detection.Label) is null) continue;
            var bounds = detection.Bounds;
            var edgeTolerance = Math.Max(2, (int)Math.Ceiling(Math.Max(pixelWidth, pixelHeight) * 0.02));
            if (bounds.Left < -edgeTolerance || bounds.Top < -edgeTolerance
                || bounds.Right > pixelWidth + edgeTolerance || bounds.Bottom > pixelHeight + edgeTolerance) continue;
            var clipped = new SKRectI(Math.Clamp(bounds.Left, 0, pixelWidth), Math.Clamp(bounds.Top, 0, pixelHeight),
                Math.Clamp(bounds.Right, 0, pixelWidth), Math.Clamp(bounds.Bottom, 0, pixelHeight));
            if (clipped.Width <= 0 || clipped.Height <= 0) continue;
            var winner = kept.FirstOrDefault(existing => IntersectionOverUnion(existing.Bounds, clipped) >= 0.8);
            if (winner is not null)
            {
                // Picture/Table ambiguity is not the same as Text/Table ambiguity: a figure
                // can really contain a table. Retain the weaker hypothesis for validation,
                // but never revive one overridden by stronger prose/heading/caption evidence.
                if (ImageLayoutPlanner.RegionKind(detection.Label) == "table"
                    && ImageLayoutPlanner.RegionKind(winner.Label) == "figure"
                    && !alternatives.Any(existing => IntersectionOverUnion(existing.Bounds, clipped) >= 0.8))
                    alternatives.Add(detection with { Bounds = clipped });
                continue;
            }
            kept.Add(detection with { Bounds = clipped });
        }

        return kept.Select(region => Map(region, false)).Concat(alternatives.Select(region => Map(region, true))).ToArray();

        PdfLayoutRegion Map(ImageLayoutRegion region, bool requiresValidation) => new(ImageLayoutPlanner.RegionKind(region.Label)!,
            new Box(region.Bounds.Left * width / pixelWidth, height - region.Bounds.Bottom * height / pixelHeight,
                region.Bounds.Right * width / pixelWidth, height - region.Bounds.Top * height / pixelHeight), requiresValidation);
    }

    private static double IntersectionOverUnion(SKRectI a, SKRectI b)
    {
        var intersection = (double)Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left))
            * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return intersection / ((double)a.Width * a.Height + (double)b.Width * b.Height - intersection);
    }
}

public sealed record PdfLayoutRegion(string Kind, Box Box, bool RequiresStructureValidation = false);
