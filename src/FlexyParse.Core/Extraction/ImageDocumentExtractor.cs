using System.Security.Cryptography;
using System.Text.Json;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace FlexyParse.Extraction;

/// <summary>Prepares a raster upload for the existing recognition and output pipeline without a PDF round trip.</summary>
public sealed class ImageDocumentExtractor(IOptions<FlexyParseOptions> options)
{
    private readonly FlexyParseOptions _options = options.Value;

    public async Task<DocumentIndex?> TryExtractAsync(JobWorkspace workspace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(workspace.SourcePath);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            if (IsImageExtension(Path.GetExtension(workspace.SourceFileName)))
            {
                throw InvalidImage();
            }

            return null;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            throw InvalidImage();
        }

        if (info.Width > _options.ImageInput.MaxDimension || info.Height > _options.ImageInput.MaxDimension
            || (long)info.Width * info.Height > _options.ImageInput.MaxPixels)
        {
            throw new DocumentConversionException(ConversionStatus.PayloadTooLarge,
                "The decoded image exceeds the configured ImageInput dimension or pixel limit.");
        }

        // Never silently discard pages or animation frames.
        if (codec.FrameCount > 1)
        {
            throw new DocumentConversionException(ConversionStatus.BadRequest,
                "Multi-frame images are not supported. Submit each page as a separate image.");
        }

        if (stream.Length > _options.ModelApi.MaxImageBytes)
        {
            throw new DocumentConversionException(ConversionStatus.PayloadTooLarge,
                "The image exceeds the configured recognition byte limit (ModelApi:MaxImageBytes).");
        }

        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            throw InvalidImage();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var origin = codec.EncodedOrigin;
        var rotated = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = rotated ? info.Height : info.Width;
        var height = rotated ? info.Width : info.Height;
        var (extension, mediaType) = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => ("jpg", "image/jpeg"),
            SKEncodedImageFormat.Png => ("png", "image/png"),
            SKEncodedImageFormat.Webp => ("webp", "image/webp"),
            _ => ("", "")
        };

        byte[] payload;
        if (extension.Length > 0 && origin == SKEncodedOrigin.TopLeft && info.AlphaType == SKAlphaType.Opaque)
        {
            // Preserve supported, correctly oriented opaque images byte-for-byte; no lossy re-encoding.
            payload = await File.ReadAllBytesAsync(workspace.SourcePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var normalized = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(normalized))
            {
                canvas.Clear(SKColors.White);
                ApplyOrigin(canvas, origin, info.Width, info.Height);
                canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var encoded = normalized.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw InvalidImage();
            payload = encoded.ToArray();
            (extension, mediaType) = ("png", "image/png");
        }

        if (payload.Length > _options.ModelApi.MaxImageBytes)
        {
            throw new DocumentConversionException(ConversionStatus.PayloadTooLarge,
                "The prepared image exceeds the configured recognition byte limit (ModelApi:MaxImageBytes).");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var image = new ImageManifestEntry
        {
            Hash = hash,
            RelativePath = Path.Combine("images", hash + "." + extension),
            MediaType = mediaType,
            Width = width,
            Height = height,
            PageNumbers = [1]
        };
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, image.RelativePath), payload, cancellationToken).ConfigureAwait(false);
        var page = new StoredPage
        {
            Number = 1,
            Width = width,
            Height = height,
            Blocks = [new ImageBlock { Id = "p1-i1", ContentHash = hash, PixelWidth = width, PixelHeight = height }]
        };
        await using (var pageStream = File.Create(workspace.PagePath(1)))
        {
            await JsonSerializer.SerializeAsync(pageStream, page, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
        }

        await using (var manifestStream = File.Create(workspace.ManifestPath))
        {
            await JsonSerializer.SerializeAsync(manifestStream, new[] { image }, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
        }

        return new DocumentIndex
        {
            SourceFileName = workspace.SourceFileName,
            Metadata = new DocumentMetadata { PageCount = 1 },
            Images = [image]
        };
    }

    private static void ApplyOrigin(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        // EXIF transforms map source pixels to a top-left-oriented destination, including mirrored orientations.
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(height, width);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, width);
                canvas.RotateDegrees(270);
                break;
        }
    }

    private static bool IsImageExtension(string extension) => extension.ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".ico" or ".wbmp"
        or ".tif" or ".tiff" or ".avif" or ".heic" or ".heif";

    private static DocumentConversionException InvalidImage() => new(ConversionStatus.BadRequest,
        "The image could not be decoded. Submit a valid single-frame PNG, JPEG, WebP, BMP, GIF or ICO image.");
}
