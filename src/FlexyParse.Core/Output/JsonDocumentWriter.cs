using System.Text.Encodings.Web;
using System.Text.Json;
using FlexyParse.Markdown;
using FlexyParse.Model;
using FlexyParse.Storage;
using TagBites.Text.Markdown;

namespace FlexyParse.Output;

public sealed class JsonDocumentWriter
{
    private readonly MarkdownJsonParser _markdownParser;

    public JsonDocumentWriter(MarkdownJsonParser markdownParser)
    {
        ArgumentNullException.ThrowIfNull(markdownParser);
        _markdownParser = markdownParser;
    }

    public async Task WriteMarkdownAsync(
        JobWorkspace workspace,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(destination);
        var markdown = await File.ReadAllTextAsync(workspace.SourcePath, cancellationToken).ConfigureAwait(false);
        await using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        writer.WriteStartObject();
        writer.WriteString("sourceFileName", workspace.SourceFileName);
        writer.WriteStartArray("pages");
        writer.WriteStartObject();
        writer.WriteNumber("number", 1);
        writer.WritePropertyName("blocks");
        _markdownParser.Write(writer, markdown, cancellationToken);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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

        await using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        writer.WriteStartObject();
        writer.WriteString("sourceFileName", index.SourceFileName);
        if (!string.IsNullOrWhiteSpace(index.Metadata.Title) || !string.IsNullOrWhiteSpace(index.Metadata.Author))
        {
            writer.WriteStartObject("metadata");
            if (!string.IsNullOrWhiteSpace(index.Metadata.Title))
            {
                writer.WriteString("title", index.Metadata.Title);
            }

            if (!string.IsNullOrWhiteSpace(index.Metadata.Author))
            {
                writer.WriteString("author", index.Metadata.Author);
            }

            writer.WriteEndObject();
        }
        writer.WritePropertyName("pages");
        writer.WriteStartArray();

        for (var number = 1; number <= index.Metadata.PageCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = await ReadPageAsync(workspace, number, cancellationToken).ConfigureAwait(false);
            page.RootElement.WriteTo(writer);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<JsonDocument> ReadPageAsync(JobWorkspace workspace, int number, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(workspace.PagePath(number), FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var page = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var content = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(content, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            await WritePageAsync(writer, workspace, page.RootElement, cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        content.Position = 0;
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task WritePageAsync(Utf8JsonWriter writer, JobWorkspace workspace, JsonElement page, CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        foreach (var property in page.EnumerateObject())
        {
            if (property.NameEquals("blocks"))
            {
                writer.WritePropertyName("blocks");
                writer.WriteStartArray();
                foreach (var block in property.Value.EnumerateArray())
                {
                    await WriteBlockAsync(writer, workspace, block, cancellationToken).ConfigureAwait(false);
                }

                writer.WriteEndArray();
                continue;
            }

            if (property.NameEquals("number"))
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private async Task WriteBlockAsync(Utf8JsonWriter writer, JobWorkspace workspace, JsonElement block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isImage = block.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "image";
        if (!isImage)
        {
            writer.WriteStartObject();
            foreach (var property in block.EnumerateObject())
            {
                if (property.Name is "type" or "text" or "level" or "rows" or "message"
                    || (property.NameEquals("firstRowIsHeader") && property.Value.ValueKind == JsonValueKind.False))
                {
                    if (property.NameEquals("text"))
                    {
                        writer.WriteString("text", ((MarkdownText)property.Value.GetString()!).Markdown);
                    }
                    else if (property.NameEquals("rows"))
                    {
                        writer.WriteStartArray("rows");
                        foreach (var row in property.Value.EnumerateArray())
                        {
                            writer.WriteStartArray();
                            foreach (var cell in row.EnumerateArray())
                            {
                                writer.WriteStringValue(((MarkdownText)cell.GetString()!).Markdown);
                            }

                            writer.WriteEndArray();
                        }

                        writer.WriteEndArray();
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }
            }

            writer.WriteEndObject();
            return;
        }

        var hash = block.TryGetProperty("contentHash", out var hashElement) && hashElement.ValueKind == JsonValueKind.String
            ? hashElement.GetString()
            : null;
        JsonDocument? recognition = null;
        string? parseError = null;
        if (!string.IsNullOrWhiteSpace(hash))
        {
            var recognitionPath = workspace.RecognitionPath(hash);
            if (File.Exists(recognitionPath))
            {
                try
                {
                    await using var stream = File.OpenRead(recognitionPath);
                    recognition = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (recognition.RootElement.ValueKind != JsonValueKind.Array || recognition.RootElement.GetArrayLength() == 0)
                    {
                        recognition.Dispose();
                        recognition = null;
                        parseError = "The recognition result could not be interpreted.";
                    }
                }
                catch (JsonException)
                {
                    parseError = "The recognition result could not be interpreted.";
                }
            }
        }

        using var recognitionContent = recognition;
        var hasNote = block.TryGetProperty("note", out var note) && !string.IsNullOrWhiteSpace(note.GetString());
        if (hasNote)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "paragraph");
            writer.WriteString("text", ((MarkdownText)note.GetString()!).Markdown);
            writer.WriteEndObject();
        }

        string? error = parseError;
        if (!string.IsNullOrWhiteSpace(hash))
        {
            var errorPath = workspace.RecognitionErrorPath(hash);
            if (File.Exists(errorPath))
            {
                error = await File.ReadAllTextAsync(errorPath, cancellationToken).ConfigureAwait(false);
            }
        }

        if (error is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "error");
            writer.WriteString("message", error);
            writer.WriteEndObject();
        }

        if (recognition is not null)
        {
            foreach (var recognizedBlock in recognition.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                recognizedBlock.WriteTo(writer);
            }
        }
    }
}
