namespace FlexyParse.Pipeline;

public enum OutputFormat
{
    Json,
    Markdown,
    Html
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
}
