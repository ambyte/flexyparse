using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace FlexyParse.Nemotron;

internal sealed record NemotronImage(DenseTensor<float> Patches, int Rows, int Columns, int ContentHeight, int ContentWidth);

internal static class NemotronImageProcessor
{
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    internal static (int Height, int Width) ResizeDimensions(int height, int width, NemotronOptions options)
    {
        if (height <= 0 || width <= 0 || height > options.MaxImageDimension || width > options.MaxImageDimension
            || (long)height * width > options.MaxImagePixels)
            throw new InvalidDataException("Nemotron input exceeds configured image limits.");
        var ratio = (double)width / height;
        var h = height;
        var w = width;
        if (h > options.MaxHeight) { h = options.MaxHeight; w = Math.Max(1, (int)(h * ratio)); }
        if (w > options.MaxWidth) { w = options.MaxWidth; h = Math.Max(1, (int)(w / ratio)); }
        return (h, w);
    }

    internal static NemotronImage Prepare(string path, NemotronOptions options, CancellationToken token)
    {
        using var input = File.OpenRead(path);
        using var managed = new SKManagedStream(input);
        using var codec = SKCodec.Create(managed) ?? throw new InvalidDataException("Unsupported Nemotron image.");
        var size = ResizeDimensions(codec.Info.Height, codec.Info.Width, options);
        token.ThrowIfCancellationRequested();
        using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Cannot decode Nemotron image.");
        using var resized = new SKBitmap(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(resized))
        using (var image = SKImage.FromBitmap(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, new SKRect(0, 0, size.Width, size.Height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }

        var rows = (size.Height + 15) / 16;
        var columns = (size.Width + 63) / 64 * 4;
        var patches = new DenseTensor<float>([rows * columns, 768]);
        // Patch order: row, column, channel, local row, local column; white top-left NaViT padding.
        for (var row = 0; row < rows; row++)
        {
            token.ThrowIfCancellationRequested();
            for (var column = 0; column < columns; column++)
            for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var px = column * 16 + x;
                var py = row * 16 + y;
                var color = px < size.Width && py < size.Height ? resized.GetPixel(px, py) : SKColors.White;
                var offset = (row * columns + column) * 768 + y * 16 + x;
                patches.Buffer.Span[offset] = (color.Red / 255f - Mean[0]) / Std[0];
                patches.Buffer.Span[offset + 256] = (color.Green / 255f - Mean[1]) / Std[1];
                patches.Buffer.Span[offset + 512] = (color.Blue / 255f - Mean[2]) / Std[2];
            }
        }

        return new(patches, rows, columns, size.Height, size.Width);
    }
}
