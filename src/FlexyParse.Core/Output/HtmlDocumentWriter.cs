using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using FlexyParse.Model;
using FlexyParse.Storage;
using Markdig;
using Markdig.Renderers;
using TagBites.Text.Markdown;

namespace FlexyParse.Output;

/// <summary>Renders the canonical JSON-derived Markdown as a standalone HTML document.</summary>
public sealed class HtmlDocumentWriter
{
    private const string DefaultStyles = """
        <style>
        :root {
            color-scheme: light;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
            font-size: 16px;
            line-height: 1.7;
            color: #243247;
            background: #f3f5f9;
        }
        *, *::before, *::after { box-sizing: border-box; }
        body {
            max-width: 960px;
            margin: 40px auto;
            padding: 48px 64px;
            background: #fff;
            border: 1px solid #e2e8f0;
            border-radius: 12px;
            box-shadow: 0 8px 32px rgb(15 23 42 / 6%);
            overflow-wrap: anywhere;
        }
        body > :first-child { margin-top: 0; }
        body > :last-child { margin-bottom: 0; }
        h1, h2, h3, h4, h5, h6 {
            margin: 1.6em 0 .6em;
            line-height: 1.3;
            font-weight: 650;
            color: #0f172a;
            text-wrap: balance;
        }
        h1 { font-size: 2em; }
        h2 { font-size: 1.55em; padding-bottom: .35em; border-bottom: 1px solid #e2e8f0; }
        h3 { font-size: 1.25em; }
        h4, h5, h6 { font-size: 1em; }
        p, ul, ol, dl, pre, blockquote, table { margin: 0 0 1.2em; }
        ul, ol { padding-left: 1.6em; }
        li + li { margin-top: .3em; }
        li > ul, li > ol { margin-top: .3em; margin-bottom: 0; }
        dt { font-weight: 650; }
        dd { margin-left: 1.6em; }
        a { color: #2563eb; text-underline-offset: .18em; }
        a:hover { color: #1d4ed8; }
        a:focus-visible { outline: 2px solid #2563eb; outline-offset: 3px; border-radius: 2px; }
        img { max-width: 100%; height: auto; border-radius: 6px; }
        blockquote {
            margin-left: 0;
            margin-right: 0;
            padding: .8em 1.2em;
            border-left: 4px solid #93b4f5;
            border-radius: 0 6px 6px 0;
            color: #475569;
            background: #f1f5fb;
        }
        blockquote > :last-child { margin-bottom: 0; }
        code, pre, kbd, samp { font-family: Consolas, "Liberation Mono", monospace; font-size: .9em; }
        code { padding: .15em .4em; border-radius: 4px; background: #eef2f6; }
        pre {
            padding: 1em 1.2em;
            overflow-x: auto;
            border: 1px solid #e2e8f0;
            border-radius: 8px;
            background: #f8fafc;
            line-height: 1.55;
            overflow-wrap: normal;
            tab-size: 4;
        }
        pre code { padding: 0; font-size: 1em; background: transparent; }
        table {
            display: block;
            max-width: 100%;
            overflow-x: auto;
            border-collapse: collapse;
            font-size: .95em;
        }
        th, td { padding: .65em 1em; border: 1px solid #dce3ed; text-align: left; vertical-align: top; }
        th { font-weight: 650; color: #0f172a; background: #edf2f9; }
        tbody tr:nth-child(even) { background: #f8fafc; }
        hr { margin: 2.5em 0; border: 0; border-top: 1px solid #dce3ed; }
        @media (max-width: 640px) {
            body { margin: 0; padding: 24px 20px; border: 0; border-radius: 0; box-shadow: none; }
            h1 { font-size: 1.7em; }
            th, td { padding: .5em .65em; }
        }
        @media print {
            :root { color: #000; background: #fff; font-size: 11pt; }
            body { max-width: none; margin: 0; padding: 0; border: 0; border-radius: 0; box-shadow: none; }
            h1, h2, h3, h4, h5, h6 { break-after: avoid; }
            pre, blockquote, tr, img { break-inside: avoid; }
            pre { white-space: pre-wrap; overflow-wrap: anywhere; }
            table { display: table; width: 100%; font-size: .85em; }
            thead { display: table-header-group; }
            a { color: inherit; }
        }
        </style>
        """;

    private readonly MarkdownDocumentWriter _markdownWriter;
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public HtmlDocumentWriter(MarkdownDocumentWriter markdownWriter)
    {
        ArgumentNullException.ThrowIfNull(markdownWriter);
        _markdownWriter = markdownWriter;
    }

    public async Task WriteAsync(
        JobWorkspace workspace, DocumentIndex index, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
        var title = string.IsNullOrWhiteSpace(index.Metadata.Title) ? index.SourceFileName : index.Metadata.Title;
        await WriteHeaderAsync(writer, title, cancellationToken).ConfigureAwait(false);
        // Keep PDF rendering page-based rather than buffering the entire document.
        for (var number = 1; number <= index.Metadata.PageCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markdown = await _markdownWriter.BuildPageAsync(workspace, index, number, cancellationToken).ConfigureAwait(false);
            await RenderAsync(writer, markdown, cancellationToken).ConfigureAwait(false);
        }

        await writer.WriteAsync("</body>\n</html>\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteMarkdownAsync(JobWorkspace workspace, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        var markdown = await _markdownWriter.BuildMarkdownSourceAsync(workspace, cancellationToken).ConfigureAwait(false);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
        await WriteHeaderAsync(writer, workspace.SourceFileName, cancellationToken).ConfigureAwait(false);
        await RenderAsync(writer, markdown, cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("</body>\n</html>\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteHeaderAsync(StreamWriter writer, string title, CancellationToken cancellationToken)
    {
        return writer.WriteAsync(("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>"
            + HtmlEncoder.Default.Encode(title) + "</title>\n" + DefaultStyles + "\n</head>\n<body>\n").AsMemory(), cancellationToken);
    }

    private async Task RenderAsync(StreamWriter writer, MarkdownDocument markdown, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        markdown.WriteTo(buffer);
        var source = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        var document = Markdig.Markdown.Parse(source, _pipeline);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(output);
        _pipeline.Setup(renderer);
        renderer.Render(document);
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteAsync(output.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
