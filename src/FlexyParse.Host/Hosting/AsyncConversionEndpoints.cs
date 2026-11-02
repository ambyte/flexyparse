using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using Microsoft.Extensions.Options;

namespace FlexyParse.Hosting;

internal static class AsyncConversionEndpoints
{
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/convert/file/async", (HttpContext context, ConversionTaskService tasks, IOptions<FlexyParseOptions> settings) =>
            SubmitAsync(context, tasks, settings.Value, null, app.Lifetime.ApplicationStopping));
        app.MapPost("/v1/convert/source/async", (HttpContext context, ConversionTaskService tasks, IOptions<FlexyParseOptions> settings, SourceDocumentService sources) =>
            SubmitAsync(context, tasks, settings.Value, sources, app.Lifetime.ApplicationStopping));
        app.MapGet("/v1/status/poll/{task_id:guid}", (Guid task_id, HttpContext context, ConversionTaskService tasks) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return tasks.GetUpdate(task_id) is { } update ? Results.Ok(update.Status)
                : Results.Problem("Task not found or expired.", statusCode: StatusCodes.Status404NotFound);
        });
        app.MapGet("/v1/result/{task_id:guid}", (Guid task_id, HttpContext context, ConversionTaskService tasks) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return tasks.GetResult(task_id);
        });
        app.MapGet("/v1/status/ws/{task_id:guid}", (Guid task_id, HttpContext context, ConversionTaskService tasks) =>
            WatchAsync(task_id, context, tasks, app.Lifetime.ApplicationStopping));
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, ConversionTaskService tasks,
        FlexyParseOptions settings, SourceDocumentService? sources, CancellationToken stopping)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var stopwatch = Stopwatch.StartNew();
        ConversionBatch? batch = null;
        try
        {
            // Reserve before reading a potentially large upload, bounding concurrent staging as well.
            using var admission = tasks.Reserve();
            batch = sources is null
                ? await ConversionBatch.ReadFilesAsync(context.Request, settings, cancellation.Token).ConfigureAwait(false)
                : await sources.StageAsync(context.Request, settings, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            var status = admission.Submit(batch, stopwatch);
            batch = null; // Ownership has passed to the task service, not the originating request.
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(status);
        }
        catch (DocumentConversionException exception)
        {
            return Results.Problem(exception.Message, statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or BadHttpRequestException)
        {
            return Results.Problem("Invalid request or unsupported parameters.", statusCode: StatusCodes.Status400BadRequest);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return Results.Problem("Submission canceled.", statusCode: StatusCodes.Status499ClientClosedRequest);
        }
        finally { batch?.Dispose(); }
    }

    private static async Task WatchAsync(Guid id, HttpContext context, ConversionTaskService tasks, CancellationToken stopping)
    {
        context.Response.Headers.CacheControl = "no-store";
        var update = tasks.GetUpdate(id);
        if (update is null)
        {
            await Results.Problem("Task not found or expired.", statusCode: 404).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Results.Problem("A WebSocket upgrade is required.", statusCode: 400).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        if (!tasks.TryOpenWebSocket())
        {
            await Results.Problem("WebSocket connection limit reached.", statusCode: 503).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            var receive = ObserveDisconnectAsync(socket, cancellation);
            try
            {
                while (update is { } current)
                {
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(current.Status).AsMemory(),
                        WebSocketMessageType.Text, true, cancellation.Token).ConfigureAwait(false);
                    if (current.Status.TaskStatus is "success" or "failure")
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Task finished.", cancellation.Token).ConfigureAwait(false);
                        // Do not cancel the outstanding Receive immediately: doing so aborts the
                        // transport and can discard the final status/close frame before the client reads it.
                        try { await receive.WaitAsync(CloseHandshakeTimeout, cancellation.Token).ConfigureAwait(false); }
                        catch (TimeoutException) { cancellation.Cancel(); }
                        break;
                    }
                    await current.Changed.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    update = tasks.GetUpdate(id);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (WebSocketException) { /* A disconnected status observer does not cancel its task. */ }
            finally
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    using var closeTimeout = new CancellationTokenSource(CloseHandshakeTimeout);
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed.", closeTimeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (closeTimeout.IsCancellationRequested) { }
                    catch (WebSocketException) { }
                }
                cancellation.Cancel();
                await receive.ConfigureAwait(false);
            }
        }
        finally { tasks.CloseWebSocket(); }
    }

    private static async Task ObserveDisconnectAsync(WebSocket socket, CancellationTokenSource cancellation)
    {
        var buffer = new byte[256];
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var message = await socket.ReceiveAsync(buffer.AsMemory(), cancellation.Token).ConfigureAwait(false);
                if (message.MessageType == WebSocketMessageType.Close) { break; }
                // The protocol is server-to-client only; do not accumulate client message bodies.
                break;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (WebSocketException) { /* Peer closed without a close frame. */ }
        finally { cancellation.Cancel(); }
    }
}
