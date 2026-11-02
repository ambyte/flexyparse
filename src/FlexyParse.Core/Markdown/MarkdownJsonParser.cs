using System.Text.Json;
using Markdig;

namespace FlexyParse.Markdown;

/// <summary>Writes compact Markdown blocks, preserving inline formatting as Markdown text.</summary>
public sealed class MarkdownJsonParser
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .EnableTrackTrivia()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    /// <summary>Writes an array of block nodes to the caller-owned JSON writer.</summary>
    public void Write(Utf8JsonWriter writer, string markdown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(markdown);
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = MarkdownDelimiterSanitizer.Collapse(markdown);
        if (MarkdownDelimiterSanitizer.MayExceedNestingDepth(prepared))
        {
            WriteProbed(writer, prepared, cancellationToken);
            return;
        }

        WriteStructured(writer, prepared, cancellationToken);
    }

    /// <summary>
    /// Reports whether the markdown can be stored as structured blocks.
    /// A negative result still has a literal-paragraph fallback in <see cref="Write"/>.
    /// </summary>
    internal bool CanStructure(string markdown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = MarkdownDelimiterSanitizer.Collapse(markdown);
        if (!MarkdownDelimiterSanitizer.MayExceedNestingDepth(prepared))
        {
            return true;
        }

        try
        {
            using var buffer = new MemoryStream();
            using var writer = new Utf8JsonWriter(buffer);
            WriteStructured(writer, prepared, cancellationToken);
            return true;
        }
        catch (ArgumentException exception) when (MarkdownDelimiterSanitizer.IsDepthLimit(exception))
        {
            return false;
        }
    }

    private void WriteProbed(Utf8JsonWriter writer, string markdown, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var probe = new Utf8JsonWriter(buffer))
        {
            try
            {
                WriteStructured(probe, markdown, cancellationToken);
                probe.Flush();
            }
            catch (ArgumentException exception) when (MarkdownDelimiterSanitizer.IsDepthLimit(exception))
            {
                WriteLiteralParagraphs(writer, markdown, cancellationToken);
                return;
            }
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        document.RootElement.WriteTo(writer);
    }

    private void WriteStructured(Utf8JsonWriter writer, string markdown, CancellationToken cancellationToken)
    {
        var document = Markdig.Markdown.Parse(markdown, _pipeline);
        var renderer = new MarkdownJsonRenderer(writer, markdown, _pipeline, cancellationToken);
        _pipeline.Setup(renderer);
        renderer.Render(document);
    }

    private static void WriteLiteralParagraphs(Utf8JsonWriter writer, string markdown, CancellationToken cancellationToken)
    {
        writer.WriteStartArray();
        var source = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var start = 0;
        while (start < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (start < source.Length && source[start] == '\n')
            {
                start++;
            }

            if (start >= source.Length)
            {
                break;
            }

            var end = source.IndexOf("\n\n", start, StringComparison.Ordinal);
            if (end < 0)
            {
                end = source.Length;
            }

            var text = MarkdownDelimiterSanitizer.EscapeLiteral(source[start..end]);
            if (text.Length > 0)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "paragraph");
                writer.WriteString("text", text);
                writer.WriteEndObject();
            }

            start = end + 2;
        }

        writer.WriteEndArray();
    }
}
