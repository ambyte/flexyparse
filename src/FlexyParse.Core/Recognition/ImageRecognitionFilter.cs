using FlexyParse.Model;
using FlexyParse.Options;

namespace FlexyParse.Recognition;

/// <summary>Filters recognition work without mutating the extraction manifest or stored images.</summary>
public static class ImageRecognitionFilter
{
    public static bool IsEligible(int width, int height, ImageRecognitionFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Unknown dimensions must retain the provider's existing validation/error handling.
        if (!options.Enabled || width <= 0 || height <= 0) return true;
        return Math.Min(width, height) >= options.MinShortSide
            && Math.Max(width, height) >= options.MinLongSide
            && (long)width * height >= options.MinPixels;
    }

    public static DocumentIndex Filter(DocumentIndex index, ImageRecognitionFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return index;
        var images = index.Images.Where(image => IsEligible(image.Width, image.Height, options)).ToArray();
        return images.Length == index.Images.Count ? index : new DocumentIndex
        {
            SourceFileName = index.SourceFileName,
            Metadata = index.Metadata,
            Images = images
        };
    }
}
