using System.Drawing;
using FlexyParse.Model;
using FlexyParse.Options;
using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;

namespace FlexyParse.Extraction;

/// <summary>
/// Renders only the visible image region, without allocating a bitmap for the whole page.
/// PDFtoImage serializes native PDFium calls internally because PDFium is not thread-safe.
/// </summary>
public sealed class PdfImageFallbackRenderer
{
    private readonly ImageRenderFallbackOptions _options;

    public PdfImageFallbackRenderer(IOptions<FlexyParseOptions> options)
    {
        _options = options.Value.ImageRenderFallback;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Dpi, 36);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.Dpi, 600);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxPixels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaxPixels, 64_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxDimension, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaxDimension, 16384);
    }

    public bool Enabled => _options.Enabled;

    public RenderedImage Render(string sourcePath, int pageNumber, double pageWidth, double pageHeight,
        Box imageBox, CancellationToken cancellationToken)
    {
        using var bitmap = RenderBitmap(sourcePath, pageNumber, pageWidth, pageHeight, imageBox, cancellationToken);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode the rendered PDF region as PNG.");
        return new RenderedImage(encoded.ToArray(), bitmap.Width, bitmap.Height);
    }

    internal SKBitmap RenderBitmap(string sourcePath, int pageNumber, double pageWidth, double pageHeight,
        Box imageBox, CancellationToken cancellationToken, int? maxPixels = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (bounds, width, height) = GetRenderGeometry(pageWidth, pageHeight, imageBox, maxPixels);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            using var stream = File.OpenRead(sourcePath);
            var bitmap = Conversion.ToImage(stream, page: pageNumber - 1, options: new RenderOptions(
                Width: width, Height: height, Bounds: bounds, DpiRelativeToBounds: true));
            if (cancellationToken.IsCancellationRequested)
            {
                bitmap.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return bitmap;
        }

        throw new PlatformNotSupportedException("PDF image fallback requires Windows, Linux or macOS.");
    }

    internal (RectangleF Bounds, int Width, int Height) GetRenderGeometry(double pageWidth, double pageHeight, Box box,
        int? maxPixels = null)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0
            || !double.IsFinite(box.Left) || !double.IsFinite(box.Bottom)
            || !double.IsFinite(box.Right) || !double.IsFinite(box.Top))
        {
            throw new InvalidOperationException("Invalid PDF page or image bounds.");
        }

        // PdfPig coordinates are already crop-relative and page-rotation adjusted, with Y pointing up.
        // PDFium renders the visible, rotated page with Y pointing down. Do not apply rotation twice.
        var left = Math.Clamp(box.Left, 0, pageWidth);
        var right = Math.Clamp(box.Right, 0, pageWidth);
        var bottom = Math.Clamp(box.Bottom, 0, pageHeight);
        var top = Math.Clamp(box.Top, 0, pageHeight);
        var regionWidth = right - left;
        var regionHeight = top - bottom;
        if (regionWidth <= 0 || regionHeight <= 0 || pageWidth > float.MaxValue || pageHeight > float.MaxValue)
        {
            throw new InvalidOperationException("Image is outside the visible PDF page.");
        }

        var pixelLimit = Math.Min(_options.MaxPixels, maxPixels ?? _options.MaxPixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelLimit, 1);
        var scale = Math.Min(_options.Dpi / 72d,
            Math.Min(_options.MaxDimension / Math.Max(regionWidth, regionHeight),
                Math.Sqrt(pixelLimit / regionWidth / regionHeight)));
        var width = Math.Max(1, (int)Math.Floor(regionWidth * scale));
        var height = Math.Max(1, (int)Math.Floor(regionHeight * scale));
        if ((long)width * height > pixelLimit)
        {
            // Extremely narrow regions can hit the one-pixel minimum in one dimension.
            width = Math.Min(width, pixelLimit / height);
            height = Math.Min(height, pixelLimit / Math.Max(1, width));
        }

        return (new RectangleF((float)left, (float)(pageHeight - top), (float)regionWidth, (float)regionHeight),
            Math.Max(1, width), Math.Max(1, height));
    }

    public sealed record RenderedImage(byte[] Bytes, int Width, int Height);
}
