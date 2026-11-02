using FlexyParse.Model;

namespace FlexyParse.Extraction;

public sealed class TextPiece
{
    public required Box Box { get; init; }

    public required string Text { get; init; }

    public double AverageFontSize { get; init; }

    public int LineCount { get; init; }
}

public sealed class TablePiece
{
    public required Box Box { get; init; }

    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
}

public sealed class ImagePiece
{
    public required Box Box { get; init; }

    public required string Id { get; init; }

    public string ContentHash { get; init; } = "";

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public string? Note { get; init; }
}
