namespace FlexyParse.Pipeline;

public enum OutputFormat
{
    Json,
    Markdown,
    Html
}

public enum ImageExportMode
{
    Placeholder,
    Embedded,
    Referenced
}

public sealed class ConversionInput
{
    public Stream? Content { get; init; }

    public string? SourcePath { get; init; }

    public bool MoveSource { get; init; }

    public required string FileName { get; init; }

    public required OutputFormat Format { get; init; }

    public bool? RecognizeImages { get; init; }

    public string? OcrLanguage { get; init; }

    // Null preserves the historical text-only Core/CLI output.
    public ImageExportMode? ImageExportMode { get; init; }

    public bool IncludeImages { get; init; } = true;

    /// <summary>Caller-owned directory for referenced image assets; references use its basename.</summary>
    public string? ImageOutputDirectory { get; init; }
}
