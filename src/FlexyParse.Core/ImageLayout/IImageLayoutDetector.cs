using SkiaSharp;

namespace FlexyParse.ImageLayout;

public sealed record ImageLayoutRegion(string Label, SKRectI Bounds, double Confidence);

public interface IImageLayoutDetector
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ImageLayoutRegion>> DetectAsync(SKBitmap image, CancellationToken cancellationToken);
}
