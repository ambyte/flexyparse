using System.Text.Json.Serialization;

namespace FlexyParse.Model;

public readonly record struct Box(double Left, double Bottom, double Right, double Top)
{
    public double Width => Math.Max(0, Right - Left);

    public double Height => Math.Max(0, Top - Bottom);

    public double Area => Width * Height;

    public double IntersectionArea(Box other)
    {
        var left = Math.Max(Left, other.Left);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Max(Bottom, other.Bottom);
        var top = Math.Min(Top, other.Top);
        if (right <= left || top <= bottom)
        {
            return 0;
        }

        return (right - left) * (top - bottom);
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ParagraphBlock), "paragraph")]
[JsonDerivedType(typeof(HeadingBlock), "heading")]
[JsonDerivedType(typeof(TableBlock), "table")]
[JsonDerivedType(typeof(ImageBlock), "image")]
[JsonDerivedType(typeof(ErrorBlock), "error")]
public abstract class ContentBlock
{
    public double Top { get; init; }

    public double Left { get; init; }
}

public sealed class ParagraphBlock : ContentBlock
{
    public required string Text { get; init; }
}

public sealed class HeadingBlock : ContentBlock
{
    public int Level { get; init; }

    public required string Text { get; init; }
}

public sealed class TableBlock : ContentBlock
{
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    /// <summary>
    /// PDF tables use the first row as a header. Field lists from an image set this to false.
    /// </summary>
    public bool? FirstRowIsHeader { get; init; }
}

public sealed class ImageBlock : ContentBlock
{
    public required string Id { get; init; }

    public string ContentHash { get; init; } = "";

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public string? Note { get; init; }
}

public sealed class ErrorBlock : ContentBlock
{
    public required string Message { get; init; }
}

public sealed class StoredPage
{
    public int Number { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public required IReadOnlyList<ContentBlock> Blocks { get; init; }
}

public sealed class DocumentMetadata
{
    public string? Title { get; init; }

    public string? Author { get; init; }

    public string? Subject { get; init; }

    public string? Keywords { get; init; }

    public string? Creator { get; init; }

    public string? Producer { get; init; }

    public string? CreationDate { get; init; }

    public string? ModificationDate { get; init; }

    public int PageCount { get; init; }
}

public sealed class ImageManifestEntry
{
    public required string Hash { get; init; }

    /// <summary>
    /// One-based page numbers containing this image. Repeated images are recognized only once.
    /// </summary>
    public List<int> PageNumbers { get; init; } = [];

    public required string RelativePath { get; init; }

    public required string MediaType { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

public sealed class DocumentIndex
{
    public required string SourceFileName { get; init; }

    public required DocumentMetadata Metadata { get; init; }

    public required IReadOnlyList<ImageManifestEntry> Images { get; init; }
}
