namespace FlexyParse.Options;

/// <summary>Minimum decoded dimensions for recognition, not image extraction or export.</summary>
public sealed class ImageRecognitionFilterOptions
{
    public bool Enabled { get; set; } = true;

    public int MinShortSide { get; set; } = 24;

    public int MinLongSide { get; set; } = 80;

    public long MinPixels { get; set; } = 4096;

    internal bool IsValid() => MinShortSide > 0 && MinLongSide >= MinShortSide && MinPixels > 0;
}
