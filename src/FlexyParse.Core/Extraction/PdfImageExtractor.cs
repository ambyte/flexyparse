using System.Security.Cryptography;
using FlexyParse.Model;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Content;

namespace FlexyParse.Extraction;

public sealed class PdfImageExtractor
{
    private readonly ILogger<PdfImageExtractor> _logger;
    private readonly PdfImageFallbackRenderer _fallbackRenderer;

    public PdfImageExtractor(ILogger<PdfImageExtractor> logger, PdfImageFallbackRenderer fallbackRenderer)
    {
        _logger = logger;
        _fallbackRenderer = fallbackRenderer;
    }

    public IReadOnlyList<ImagePiece> Extract(
        Page page,
        JobWorkspace workspace,
        IDictionary<string, ImageManifestEntry> uniqueImages,
        int maxImageBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(uniqueImages);
        var pieces = new List<ImagePiece>();
        var index = 0;
        foreach (var image in page.GetImages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (image.IsImageMask || image.WidthInSamples < 8 || image.HeightInSamples < 8)
            {
                continue;
            }

            index++;
            var id = $"p{page.Number}-i{index}";
            var bounds = image.BoundingBox;
            // A rotated PdfRectangle's named edges are not necessarily its axis-aligned bounds.
            var corners = new[] { bounds.BottomLeft, bounds.BottomRight, bounds.TopLeft, bounds.TopRight };
            var box = new Box(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
            var pixelWidth = image.WidthInSamples;
            var pixelHeight = image.HeightInSamples;
            var extracted = false;
            byte[] payload = [];
            var extension = "";
            var mediaType = "";
            try
            {
                extracted = TryGetPayload(image, out payload, out extension, out mediaType);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            {
                _logger.LogWarning(exception, "Could not decode image {ImageId} on page {Page}; trying PDF render fallback.", id, page.Number);
            }

            if (!extracted && _fallbackRenderer.Enabled)
            {
                try
                {
                    var rendered = _fallbackRenderer.Render(workspace.SourcePath, page.Number, page.Width, page.Height, box, cancellationToken);
                    payload = rendered.Bytes;
                    extension = "png";
                    mediaType = "image/png";
                    pixelWidth = rendered.Width;
                    pixelHeight = rendered.Height;
                    extracted = true;
                    _logger.LogInformation("Rendered image {ImageId} on page {Page} of {File} using PDFtoImage fallback ({Width}x{Height}).",
                        id, page.Number, workspace.SourceFileName, pixelWidth, pixelHeight);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
                {
                    _logger.LogWarning(exception, "PDF render fallback failed for image {ImageId} on page {Page} of {File}.",
                        id, page.Number, workspace.SourceFileName);
                }
            }

            if (!extracted)
            {
                _logger.LogDebug("Skipped image {ImageId} on page {Page}: unsupported encoding.", id, page.Number);
                pieces.Add(new ImagePiece
                {
                    Id = id,
                    Box = box,
                    PixelWidth = image.WidthInSamples,
                    PixelHeight = image.HeightInSamples,
                    Note = "The image format is not supported for recognition."
                });
                continue;
            }

            if (payload.Length > maxImageBytes)
            {
                _logger.LogInformation(
                    "Skipped image {ImageId} on page {Page}: {Bytes} bytes exceeds the recognition limit.",
                    id,
                    page.Number,
                    payload.Length);
                pieces.Add(new ImagePiece
                {
                    Id = id,
                    Box = box,
                    PixelWidth = image.WidthInSamples,
                    PixelHeight = image.HeightInSamples,
                    Note = "The image is larger than the recognition limit and was not sent to the model."
                });
                continue;
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
            var fileName = hash + "." + extension;
            var relativePath = Path.Combine("images", fileName);
            var fullPath = Path.Combine(workspace.Root, relativePath);
            if (!uniqueImages.TryGetValue(hash, out var manifestEntry))
            {
                File.WriteAllBytes(fullPath, payload);
                manifestEntry = new ImageManifestEntry
                {
                    Hash = hash,
                    RelativePath = relativePath,
                    MediaType = mediaType,
                    Width = pixelWidth,
                    Height = pixelHeight
                };
                uniqueImages.Add(hash, manifestEntry);
            }

            if (!manifestEntry.PageNumbers.Contains(page.Number))
            {
                manifestEntry.PageNumbers.Add(page.Number);
            }

            pieces.Add(new ImagePiece
            {
                Id = id,
                Box = box,
                ContentHash = hash,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight
            });
        }

        return pieces;
    }

    private static bool TryGetPayload(IPdfImage image, out byte[] payload, out string extension, out string mediaType)
    {
        if (image.TryGetPng(out var png) && png is { Length: > 0 })
        {
            payload = png;
            extension = "png";
            mediaType = "image/png";
            return true;
        }

        var raw = image.RawBytes;
        if (raw.Length >= 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF)
        {
            payload = raw.ToArray();
            extension = "jpg";
            mediaType = "image/jpeg";
            return true;
        }

        payload = [];
        extension = "";
        mediaType = "";
        return false;
    }
}
