using SkiaSharp;

namespace FlexyParse.Nemotron;

/// <summary>Bounded, aspect-preserving recovery rendering; never overwrites or downsizes the source.</summary>
internal static class NemotronRecoveryImage
{
    internal static bool TryGetSize(int width, int height, NemotronOptions options, out SKSizeI size)
    {
        size = default;
        if (width <= 0 || height <= 0) return false;
        var border = options.RepetitionRecoveryPaddingPixels * 2;
        var maxWidth = Math.Min(options.MaxWidth, options.MaxImageDimension);
        var maxHeight = Math.Min(options.MaxHeight, options.MaxImageDimension);
        var maxPixels = Math.Min(options.RepetitionRecoveryMaxPixels, options.MaxImagePixels);
        if ((long)width + border > maxWidth || (long)height + border > maxHeight
            || ((long)width + border) * ((long)height + border) > maxPixels) return false;

        var scale = Math.Min(options.RepetitionRecoveryScale,
            Math.Min((maxWidth - border) / (double)width, (maxHeight - border) / (double)height));
        // Include the border in the pixel budget. Bisection avoids overflow and aspect-ratio distortion.
        var lower = 1d;
        var upper = scale;
        for (var i = 0; i < 32; i++)
        {
            var candidate = (lower + upper) / 2;
            var area = (width * candidate + border) * (height * candidate + border);
            if (area <= maxPixels) lower = candidate;
            else upper = candidate;
        }
        // Prefer the exact requested scale when it fits, avoiding floating-point off-by-one at 3x.
        if ((width * scale + border) * (height * scale + border) <= maxPixels) lower = scale;
        size = new SKSizeI((int)Math.Floor(width * lower) + border, (int)Math.Floor(height * lower) + border);
        return true;
    }

    internal static SKBitmap Render(SKBitmap source, SKRectI bounds, SKSizeI size, int padding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var crop = new SKBitmap();
        if (!source.ExtractSubset(crop, bounds))
            throw new InvalidOperationException("Could not crop a Nemotron recovery region.");
        var result = new SKBitmap(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        try
        {
            using var canvas = new SKCanvas(result);
            using var image = SKImage.FromBitmap(crop);
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, new SKRect(padding, padding, size.Width - padding, size.Height - padding),
                new SKSamplingOptions(SKCubicResampler.CatmullRom));
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
