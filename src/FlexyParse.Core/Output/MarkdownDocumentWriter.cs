using System.Globalization;
using System.Text.Json;
using FlexyParse.Markdown;
using FlexyParse.Model;
using FlexyParse.Storage;
using TagBites.Text.Markdown;

namespace FlexyParse.Output;

public sealed class MarkdownDocumentWriter
{
    private readonly JsonDocumentWriter _jsonWriter;
    private readonly MarkdownJsonFormatter _formatter;

    public MarkdownDocumentWriter(JsonDocumentWriter jsonWriter, MarkdownJsonFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(jsonWriter);
        ArgumentNullException.ThrowIfNull(formatter);
        _jsonWriter = jsonWriter;
        _formatter = formatter;
    }

    public async Task WriteAsync(
        JobWorkspace workspace,
        DocumentIndex index,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(destination);

        for (var number = 1; number <= index.Metadata.PageCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await BuildPageAsync(workspace, index, number, cancellationToken).ConfigureAwait(false);
            document.WriteTo(destination);
            destination.WriteByte((byte)'\n');
        }
    }

    internal async Task<MarkdownDocument> BuildPageAsync(
        JobWorkspace workspace, DocumentIndex index, int number, CancellationToken cancellationToken)
    {
        using var page = await _jsonWriter.ReadPageAsync(workspace, number, cancellationToken).ConfigureAwait(false);
        var document = new MarkdownDocument();
        if (number == 1)
        {
            document.FrontMatter = BuildFrontMatter(index);
        }
        else
        {
            document.AddThematicBreak();
        }

        document.AddParagraph(MarkdownText.Bold("Page " + page.RootElement.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture)));
        _formatter.AppendTo(document, page.RootElement.GetProperty("blocks"), cancellationToken);
        return document;
    }

    internal async Task<MarkdownDocument> BuildMarkdownSourceAsync(JobWorkspace workspace, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await _jsonWriter.WriteMarkdownAsync(workspace, buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
        var document = new MarkdownDocument();
        _formatter.AppendTo(document, json.RootElement.GetProperty("pages")[0].GetProperty("blocks"), cancellationToken);
        return document;
    }

    private static MarkdownFrontMatter BuildFrontMatter(DocumentIndex index)
    {
        var frontMatter = new MarkdownFrontMatter
        {
            Title = string.IsNullOrWhiteSpace(index.Metadata.Title) ? index.SourceFileName : index.Metadata.Title
        };
        frontMatter["source"] = index.SourceFileName;
        frontMatter["pages"] = index.Metadata.PageCount.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(index.Metadata.Author))
        {
            frontMatter["author"] = index.Metadata.Author;
        }

        return frontMatter;
    }
}
