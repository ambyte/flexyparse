using FlexyParse.ImageLayout;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace FlexyParse.Nemotron;

/// <summary>A suspect periodic tail is not proof that the source text is invalid.</summary>
internal sealed class NemotronRepetitionException(string message) : IOException(message)
{
    internal int GeneratedTokenCount { get; init; }
    internal int RepetitionPeriod { get; init; }
    // Diagnostic metadata only; never log decoded source text automatically.
    internal IReadOnlyList<int> RepeatedTokenIds { get; init; } = [];
}

internal static class NemotronRepetitionRecovery
{
    private const int RecoveryMinimumSide = 32;
    private const int RecoveryMinimumGap = 2;
    private const double RecoveryMaxInkRatio = 0.02;

    internal static async Task<string> RecognizeAsync(string path, NemotronOptions options,
        Func<string, CancellationToken, Task<string>> recognize, ILogger logger, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var content = await recognize(path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return content;
        }
        catch (NemotronRepetitionException) when (options.RetryRepetitiveImages)
        {
            token.ThrowIfCancellationRequested();
            using var input = File.OpenRead(path);
            using var managed = new SKManagedStream(input);
            using var codec = SKCodec.Create(managed);
            if (codec is null) throw;
            // Validate dimensions before allocating the recovery bitmap too.
            _ = NemotronImageProcessor.ResizeDimensions(codec.Info.Height, codec.Info.Width, options);
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null) throw;
            var upscale = options.UpscaleRepetitiveImages;
            var tiles = upscale
                ? ImageWhitespaceSplitter.Split(bitmap, options.RepetitionRecoveryMaxRegions, token,
                    RecoveryMinimumSide, RecoveryMinimumGap, RecoveryMaxInkRatio)
                : ImageWhitespaceSplitter.Split(bitmap, 2, token);
            if (!upscale && tiles.Count < 2) throw;
            var sizes = new SKSizeI[tiles.Count];
            for (var i = 0; i < tiles.Count; i++)
            {
                var bounds = tiles[i].Bounds;
                if (upscale)
                {
                    if (!NemotronRecoveryImage.TryGetSize(bounds.Width, bounds.Height, options, out sizes[i]))
                    {
                        logger.LogWarning("Nemotron repetition recovery skipped: a region cannot fit the configured image limits without downsizing.");
                        throw;
                    }
                }
                else sizes[i] = new SKSizeI(bounds.Width, bounds.Height);
            }

            logger.LogWarning("Nemotron detected a periodic output tail; retrying {RegionCount} nonoverlapping regions with image preprocessing {UpscaleEnabled}. OCR may be approximate; repeated source text will not be deduplicated.",
                tiles.Count, upscale);
            var results = new List<string>(tiles.Count);
            for (var i = 0; i < tiles.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var tile = tiles[i];
                var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "nemotron-recovery-" + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    using var crop = NemotronRecoveryImage.Render(bitmap, tile.Bounds, sizes[i],
                        upscale ? options.RepetitionRecoveryPaddingPixels : 0, token);
                    using var encoded = crop.Encode(SKEncodedImageFormat.Png, 100)
                        ?? throw new InvalidOperationException("Could not encode a Nemotron recovery tile.");
                    await File.WriteAllBytesAsync(temporary, encoded.ToArray(), token).ConfigureAwait(false);
                    logger.LogInformation("Nemotron recovery region {RegionIndex}/{RegionCount}: {SourceWidth}x{SourceHeight} to {Width}x{Height}; timeout {TimeoutSeconds}s.",
                        i + 1, tiles.Count, tile.Bounds.Width, tile.Bounds.Height, crop.Width, crop.Height,
                        options.RepetitionRecoveryTimeoutSeconds);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(options.RepetitionRecoveryTimeoutSeconds));
                    // Deliberately call the engine, not this helper: retries cannot recurse.
                    try
                    {
                        var content = await recognize(temporary, deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        results.Add(content);
                    }
                    catch (OperationCanceledException exception) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                    {
                        throw new TimeoutException("Nemotron image repetition recovery exceeded its per-region deadline; incomplete OCR was discarded.", exception);
                    }
                }
                finally { File.Delete(temporary); }
            }
            token.ThrowIfCancellationRequested();
            logger.LogInformation("Nemotron repetition recovery completed for {RegionCount} regions. Preprocessed OCR may contain transcription errors.", tiles.Count);
            return string.Join("\n\n", results);
        }
    }
}
