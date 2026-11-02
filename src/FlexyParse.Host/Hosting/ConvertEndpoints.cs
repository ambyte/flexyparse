using System.Diagnostics;
using System.Text.Json;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using Microsoft.Extensions.Options;

namespace FlexyParse.Hosting;

public static class ConvertEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/v1/capabilities", (IOptions<FlexyParseOptions> settings) => Results.Ok(new
        {
            from_formats = new[] { "pdf", "md", "png", "jpg", "jpeg", "webp", "bmp", "gif", "ico" },
            to_formats = new[] { "md", "json", "html" },
            sources = new[] { "files", "http_sources", "file_sources" },
            options = new[] { "to_formats", "do_ocr", "ocr_language" },
            // Gotenberg accepts additional formats; FlexyParse does not maintain its format list.
            gotenberg_enabled = !string.IsNullOrWhiteSpace(settings.Value.GotenbergUrl),
            async_support = true,
            limits = new { max_file_size = settings.Value.MaxUploadBytes, max_num_files = settings.Value.MaxFilesPerRequest }
        }));
        app.MapPost("/v1/convert/file", (HttpContext context, IDocumentConverter converter, IOptions<FlexyParseOptions> settings) =>
            ConvertAsync(context, converter, settings.Value, null, app.Lifetime.ApplicationStopping));
        app.MapPost("/v1/convert/source", (HttpContext context, IDocumentConverter converter, IOptions<FlexyParseOptions> settings, SourceDocumentService sources) =>
            ConvertAsync(context, converter, settings.Value, sources, app.Lifetime.ApplicationStopping));
        AsyncConversionEndpoints.Map(app);
    }

    private static async Task<IResult> ConvertAsync(HttpContext context, IDocumentConverter converter,
        FlexyParseOptions settings, SourceDocumentService? sources, CancellationToken stopping)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var token = cancellation.Token;
        ConversionBatch? batch = null;
        var transferred = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            batch = sources is null
                ? await ConversionBatch.ReadFilesAsync(context.Request, settings, token).ConfigureAwait(false)
                : await sources.ReadAsync(context.Request, settings, token).ConfigureAwait(false);
            var result = await ConversionResultBuilder.BuildAsync(batch, converter, settings, stopwatch, token).ConfigureAwait(false);
            var stream = new CleanupDirectoryStream(result.Path, batch);
            transferred = true;
            return Results.Stream(stream, result.ContentType, result.DownloadName);
        }
        catch (DocumentConversionException exception)
        {
            return Results.Problem(exception.Message, statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or BadHttpRequestException)
        {
            return Results.Problem("Invalid request or unsupported parameters.", statusCode: StatusCodes.Status400BadRequest);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Results.Problem("Processing canceled.", statusCode: StatusCodes.Status499ClientClosedRequest);
        }
        finally
        {
            if (!transferred)
            {
                batch?.Dispose();
            }
        }
    }
}

internal sealed class CleanupDirectoryStream(string path, ConversionBatch batch)
    : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous)
{
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            batch.Dispose();
        }
    }
}
