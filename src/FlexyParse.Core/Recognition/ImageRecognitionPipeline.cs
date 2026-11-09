using System.Text;
using FlexyParse.ImageLayout;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlexyParse.Recognition;

/// <summary>Provider-independent layout, ordered region recognition and result assembly.
/// Callbacks return Markdown or plain text consistently; the provider owns conversion to document blocks.</summary>
public sealed class ImageRecognitionPipeline(
    IOptions<FlexyParseOptions> options,
    ImageLayoutProcessor? layoutProcessor = null,
    ILogger<ImageRecognitionPipeline>? logger = null)
{
    private readonly FlexyParseOptions _options = options.Value;

    public async Task<string> RecognizeAsync(byte[] bytes, JobWorkspace workspace, ImageManifestEntry image,
        Func<byte[], string, string?, CancellationToken, Task<string>> recognize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(recognize);
        cancellationToken.ThrowIfCancellationRequested();
        using var plan = layoutProcessor is null ? null
            : await layoutProcessor.PrepareAsync(bytes, cancellationToken, image, workspace).ConfigureAwait(false);
        if (plan is null)
            return await recognize(bytes, image.MediaType, null, cancellationToken).ConfigureAwait(false);

        // Validate every crop before invoking a provider: a late oversized crop must not cause
        // duplicate API requests or mix partial region output with whole-image output.
        var crops = new List<(byte[] Bytes, string Label)>();
        foreach (var region in plan.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = plan.CropBounds(region);
            if (!ImageRecognitionFilter.IsEligible(bounds.Width, bounds.Height, _options.ImageRecognitionFilter))
            {
                logger?.LogInformation("Skipped small layout region {RegionLabel} ({Width}x{Height}) of image {ImageHash} in job {JobId}.",
                    region.Label, bounds.Width, bounds.Height, image.Hash, workspace.JobId);
                continue;
            }
            var crop = plan.Crop(region);
            if (crop.Length > _options.ModelApi.MaxImageBytes)
            {
                logger?.LogWarning("A layout crop exceeds the image byte limit. Using whole-image recognition for {ImageHash} in job {JobId}.",
                    image.Hash, workspace.JobId);
                return await recognize(bytes, image.MediaType, null, cancellationToken).ConfigureAwait(false);
            }
            crops.Add((crop, region.Label));
        }

        var result = new StringBuilder();
        for (var i = 0; i < crops.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var crop = crops[i];
            logger?.LogInformation(
                "Recognizing layout region {RegionIndex}/{RegionCount} ({RegionLabel}) of image {ImageHash} in document {File}, job {JobId}.",
                i + 1, crops.Count, crop.Label, image.Hash, workspace.SourceFileName, workspace.JobId);
            var content = await recognize(crop.Bytes, "image/png", ImageLayoutPlanner.RegionKind(crop.Label), cancellationToken)
                .ConfigureAwait(false);
            if (result.Length > 0) result.Append("\n\n");
            result.Append(content);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result.ToString();
    }

    /// <summary>Adapts file-based local engines to the same pipeline. Crop files are private to
    /// this invocation and are removed after success, failure or cancellation.</summary>
    public async Task<string> RecognizeFileAsync(JobWorkspace workspace, ImageManifestEntry image,
        Func<string, string, string?, CancellationToken, Task<string>> recognize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(recognize);
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = Path.Combine(workspace.Root, image.RelativePath);
        // Preserve local engines' streaming validation when layout is disabled.
        if (layoutProcessor is null || !_options.ImageLayout.Enabled)
            return await recognize(sourcePath, image.MediaType, null, cancellationToken).ConfigureAwait(false);

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return await RecognizeAsync(bytes, workspace, image, async (content, mediaType, kind, token) =>
        {
            if (ReferenceEquals(content, bytes))
                return await recognize(sourcePath, mediaType, kind, token).ConfigureAwait(false);
            var temporary = Path.Combine(workspace.Root, "recognition-crop-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                await File.WriteAllBytesAsync(temporary, content, token).ConfigureAwait(false);
                return await recognize(temporary, mediaType, kind, token).ConfigureAwait(false);
            }
            finally { File.Delete(temporary); }
        }, cancellationToken).ConfigureAwait(false);
    }
}
