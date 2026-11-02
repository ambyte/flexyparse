using FlexyParse.Options;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Enums;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.Models;

namespace FlexyParse.ImageLayout;

/// <summary>One CPU session, initialized at Host startup or lazily by library consumers.</summary>
public sealed class YoloImageLayoutDetector(IOptions<FlexyParseOptions> options) : IImageLayoutDetector, IDisposable
{
    private readonly ImageLayoutOptions _options = options.Value.ImageLayout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Yolo? _yolo;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Initialize(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ImageLayoutRegion>> DetectAsync(SKBitmap image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Initialize(cancellationToken);
            var detections = _yolo!.RunObjectDetection(image, confidence: _options.Confidence, iou: _options.IoU);
            cancellationToken.ThrowIfCancellationRequested();
            return detections.Select(result => new ImageLayoutRegion(
                result.Label.Name, result.BoundingBox, result.Confidence)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.IsValid() || !_options.Enabled)
        {
            throw new InvalidOperationException("Image layout detection is disabled or incorrectly configured.");
        }

        if (_yolo is null)
        {
            var yolo = new Yolo(new YoloOptions
            {
                ExecutionProvider = new CpuExecutionProvider(Path.GetFullPath(_options.ModelPath)),
                ImageResize = Enum.Parse<ImageResize>(_options.ImageResize)
            });
            try
            {
                if (!yolo.OnnxModel.CustomMetaData.TryGetValue("names", out var names))
                {
                    throw new InvalidDataException("The layout model has no class-name metadata.");
                }

                NormalizeLabels(yolo.OnnxModel.Labels, names);
                _yolo = yolo;
            }
            catch
            {
                yolo.Dispose();
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void NormalizeLabels(LabelModel[] labels, string names)
    {
        // Some exports serialize Python dictionary keys as 0,1,10,2,... . YoloDotNet 4.2
        // assigns sequential indices in that order. Its decoder then indexes this array by
        // the predicted numeric class ID, turning Text into Table unless we repair the map.
        var matches = Regex.Matches(names, "(?:^|[,{])\\s*['\"]?(?<id>[0-9]+)['\"]?\\s*:\\s*['\"](?<name>[^'\"]+)['\"]",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var ordered = new LabelModel?[labels.Length];
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id >= ordered.Length || ordered[id] is not null)
            {
                throw new InvalidDataException("The layout model class IDs must be unique and contiguous from zero.");
            }

            ordered[id] = new LabelModel { Index = id, Name = match.Groups["name"].Value };
        }

        if (ordered.Length == 0 || matches.Count != ordered.Length || ordered.Any(label => label is null))
        {
            throw new InvalidDataException("The layout model class metadata does not match its labels.");
        }

        for (var i = 0; i < labels.Length; i++) labels[i] = ordered[i]!;
    }

    public void Dispose()
    {
        _yolo?.Dispose();
        _gate.Dispose();
    }
}
