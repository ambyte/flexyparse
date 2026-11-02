using System.Text.Json;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Roundtrip;
using Markdig.Syntax;

namespace FlexyParse.Markdown;

internal sealed class MarkdownJsonRenderer : RendererBase
{
    private readonly string _source;
    private readonly MarkdownPipeline _pipeline;
    private readonly CancellationToken _cancellationToken;

    public MarkdownJsonRenderer(Utf8JsonWriter writer, string source, MarkdownPipeline pipeline, CancellationToken cancellationToken)
    {
        Writer = writer;
        _source = source;
        _pipeline = pipeline;
        _cancellationToken = cancellationToken;
        ObjectRenderers.Add(new MarkdownBlockJsonRenderer());
    }

    public Utf8JsonWriter Writer { get; }

    public override object Render(MarkdownObject markdownObject)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (markdownObject is MarkdownDocument document)
        {
            WriteBlocks(document);
        }
        else
        {
            Write(markdownObject);
        }

        return Writer;
    }

    public void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();

    public void WriteBlocks(ContainerBlock container)
    {
        Writer.WriteStartArray();
        WriteBlockContents(container);
        Writer.WriteEndArray();
    }

    private void WriteBlockContents(ContainerBlock container)
    {
        foreach (var child in container)
        {
            if (child is not EmptyBlock)
            {
                Write(child);
            }
        }
    }

    public string InlineText(LeafBlock block)
    {
        using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var renderer = new RoundtripRenderer(text);
        _pipeline.Setup(renderer);
        if (block.Inline is not null)
        {
            renderer.Render(block.Inline);
        }

        return RemoveContainerIndentation(text.ToString(), ContainerIndentation(block), skipFirstLine: true).Trim();
    }

    public string CodeText(CodeBlock block) =>
        RemoveContainerIndentation(block.Lines.ToString(), ContainerIndentation(block), skipFirstLine: false);

    private int ContainerIndentation(Block block)
    {
        var parent = block.Parent;
        while (parent is not null && parent is not QuoteBlock)
        {
            parent = parent.Parent;
        }

        if (parent is null || block.Span.Start <= 0)
        {
            return block.Column;
        }

        // Quote markers are already removed by Markdig; only whitespace after the last marker remains.
        var lineStart = _source.LastIndexOf('\n', block.Span.Start - 1) + 1;
        var prefix = _source.AsSpan(lineStart, block.Span.Start - lineStart);
        var quote = prefix.LastIndexOf('>');
        if (quote < 0)
        {
            return block.Column;
        }

        var end = quote + 1;
        if (end < prefix.Length && prefix[end] is ' ' or '\t')
        {
            end++;
        }

        var column = 0;
        foreach (var character in prefix[..end])
        {
            column += character == '\t' ? 4 - column % 4 : 1;
        }

        return Math.Max(0, block.Column - column);
    }

    private static string RemoveContainerIndentation(string text, int indentation, bool skipFirstLine)
    {
        if (indentation == 0)
        {
            return text;
        }

        // Roundtrip trivia includes the containing list's indentation; JSON stores local content.
        return string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((line, index) =>
        {
            var offset = 0;
            if (!skipFirstLine || index != 0)
            {
                while (offset < Math.Min(indentation, line.Length) && line[offset] == ' ')
                {
                    offset++;
                }
            }

            return line[offset..];
        }));
    }

    public string SourceText(MarkdownObject node)
    {
        var span = node.Span;
        return span.Start >= 0 && span.End >= span.Start && span.End < _source.Length
            ? _source.Substring(span.Start, span.Length)
            : "";
    }
}
