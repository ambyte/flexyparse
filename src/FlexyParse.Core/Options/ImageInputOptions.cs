namespace FlexyParse.Options;

public sealed class ImageInputOptions
{
    public int MaxDimension { get; set; } = 16384;

    public long MaxPixels { get; set; } = 40_000_000;
}
