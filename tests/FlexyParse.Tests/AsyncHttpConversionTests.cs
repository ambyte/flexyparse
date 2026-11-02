using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FlexyParse.Hosting;
using FlexyParse.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace FlexyParse.Tests;

public sealed partial class HttpConversionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AsyncConversionReturnsRetainedJsonOrZip(bool source, bool multiple)
    {
        await using var server = await TestServer.StartAsync();
        HttpResponseMessage submitted;
        if (source)
        {
            submitted = await server.Client.PostAsJsonAsync("/v1/convert/source/async", new
            {
                file_sources = multiple
                    ? new[] { Base64Source("# First", "first.md"), Base64Source("# Second", "second.md") }
                    : new[] { Base64Source("# First", "first.md") }
            });
        }
        else
        {
            using var form = Form("# First", "first.md");
            if (multiple) { form.Add(new StringContent("# Second"), "files", "second.md"); }
            submitted = await server.Client.PostAsync("/v1/convert/file/async", form);
        }
        using (submitted)
        {
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            var initial = await submitted.Content.ReadFromJsonAsync<ConversionTaskStatus>();
            Assert.NotNull(initial);
            Assert.Equal("pending", initial.TaskStatus);
            Assert.True(initial.TaskPosition > 0);
            Assert.Null(initial.TaskMeta);
            await WaitForTaskAsync(server.Client, initial.TaskId, "success");
            byte[]? first = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var result = await server.Client.GetAsync($"/v1/result/{initial.TaskId}");
                result.EnsureSuccessStatusCode();
                Assert.True(result.Headers.CacheControl?.NoStore);
                var bytes = await result.Content.ReadAsByteArrayAsync();
                if (first is not null) { Assert.Equal(first, bytes); }
                first = bytes;
                if (multiple)
                {
                    Assert.Equal("application/zip", result.Content.Headers.ContentType?.MediaType);
                    using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                    Assert.Equal(["first.md", "second.md"], archive.Entries.Select(entry => entry.Name));
                }
                else
                {
                    using var json = JsonDocument.Parse(bytes);
                    Assert.Equal("# First", json.RootElement.GetProperty("document").GetProperty("md_content").GetString());
                }
            }
        }
    }

    [Fact]
    public async Task PendingTasksHaveQueuePositionsAndQueueCapacityIsEnforced()
    {
        var converter = new TaskGateConverter();
        await using var server = await TestServer.StartAsync(services =>
        {
            services.AddSingleton<IDocumentConverter>(converter);
            services.Configure<AsyncConversionOptions>(options => { options.WorkerCount = 1; options.QueueCapacity = 2; });
        });
        var first = await SubmitTaskAsync(server.Client);
        await converter.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var notReady = await server.Client.GetAsync($"/v1/result/{first}");
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        var second = await SubmitTaskAsync(server.Client);
        var third = await SubmitTaskAsync(server.Client);
        Assert.Equal(1, (await PollAsync(server.Client, second)).TaskPosition);
        Assert.Equal(2, (await PollAsync(server.Client, third)).TaskPosition);
        using var form = Form("# Fourth", "fourth.md");
        using var full = await server.Client.PostAsync("/v1/convert/file/async", form);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
        converter.Release.TrySetResult();
        await WaitForTaskAsync(server.Client, third, "success");
    }

    [Fact]
    public async Task AcceptedTaskOutlivesSubmissionCancellation()
    {
        var converter = new TaskGateConverter();
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(converter));
        using var cancellation = new CancellationTokenSource();
        using var form = Form("# Background", "background.md");
        using var response = await server.Client.PostAsync("/v1/convert/file/async", form, cancellation.Token);
        response.EnsureSuccessStatusCode();
        var task = await response.Content.ReadFromJsonAsync<ConversionTaskStatus>();
        cancellation.Cancel();
        await converter.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(converter.Cancelled.Task.IsCompleted);
        converter.Release.TrySetResult();
        await WaitForTaskAsync(server.Client, task!.TaskId, "success");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSocketReportsStartedThenTerminalStatus(bool failure)
    {
        var converter = new TaskGateConverter(failure);
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(converter), TestApiKey);
        server.Client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        var id = await SubmitTaskAsync(server.Client);
        await converter.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Api-Key", TestApiKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(SocketUri(server.Client, id), timeout.Token);
        Assert.Equal("started", (await ReceiveStatusAsync(socket, timeout.Token)).TaskStatus);
        converter.Release.TrySetResult();
        var final = await ReceiveStatusAsync(socket, timeout.Token);
        Assert.Equal(failure ? "failure" : "success", final.TaskStatus);
        Assert.Null(final.TaskPosition);
        var close = await socket.ReceiveAsync(new byte[512], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
        using var result = await server.Client.GetAsync($"/v1/result/{id}");
        Assert.Equal(failure ? HttpStatusCode.BadRequest : HttpStatusCode.OK, result.StatusCode);
        if (failure)
        {
            Assert.Contains("Controlled conversion failure", await result.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(converter.UploadDirectory));
        }
    }

    [Fact]
    public async Task WebSocketDisconnectDoesNotCancelTaskAndCompletedTaskCanBeWatched()
    {
        var converter = new TaskGateConverter();
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(converter));
        var id = await SubmitTaskAsync(server.Client);
        await converter.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(SocketUri(server.Client, id), timeout.Token);
            Assert.Equal("started", (await ReceiveStatusAsync(socket, timeout.Token)).TaskStatus);
            socket.Abort();
        }
        converter.Release.TrySetResult();
        await WaitForTaskAsync(server.Client, id, "success");
        using var completedSocket = new ClientWebSocket();
        await completedSocket.ConnectAsync(SocketUri(server.Client, id), timeout.Token);
        Assert.Equal("success", (await ReceiveStatusAsync(completedSocket, timeout.Token)).TaskStatus);
    }

    [Fact]
    public async Task ExpirationDeletesResultsAndReleasesRetainedTaskCapacity()
    {
        var clock = new TaskTestClock();
        var converter = new TaskGateConverter();
        converter.Release.TrySetResult();
        await using var server = await TestServer.StartAsync(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IDocumentConverter>(converter);
            services.Configure<AsyncConversionOptions>(options => { options.MaxStoredTasks = 1; options.ResultRetentionSeconds = 5; });
        });
        var id = await SubmitTaskAsync(server.Client);
        await WaitForTaskAsync(server.Client, id, "success");
        var directory = converter.UploadDirectory!;
        Assert.Equal("result", Assert.Single(Directory.GetFiles(directory)).Split(Path.DirectorySeparatorChar).Last());
        using (var download = await server.Client.GetAsync($"/v1/result/{id}"))
        {
            download.EnsureSuccessStatusCode();
            _ = await download.Content.ReadAsStringAsync();
        }
        using var overflowForm = Form("# Overflow", "overflow.md");
        using var overflow = await server.Client.PostAsync("/v1/convert/file/async", overflowForm);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, overflow.StatusCode);
        clock.Advance(TimeSpan.FromSeconds(6));
        using var expired = await server.Client.GetAsync($"/v1/result/{id}");
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.False(Directory.Exists(directory));
        var next = await SubmitTaskAsync(server.Client);
        await WaitForTaskAsync(server.Client, next, "success");
    }

    [Fact]
    public async Task ShutdownCancelsRunningTasksAndCleansQueuedUploads()
    {
        var converter = new TaskGateConverter();
        await using var server = await TestServer.StartAsync(services =>
        {
            services.AddSingleton<IDocumentConverter>(converter);
            services.Configure<AsyncConversionOptions>(options => options.WorkerCount = 1);
        });
        _ = await SubmitTaskAsync(server.Client);
        await converter.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _ = await SubmitTaskAsync(server.Client);
        await server.StopAsync();
        await converter.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(converter.UploadDirectory));
    }

    [Theory]
    [InlineData("/v1/convert/file/async", "POST")]
    [InlineData("/v1/convert/source/async", "POST")]
    [InlineData("/v1/status/poll/00000000-0000-0000-0000-000000000001", "GET")]
    [InlineData("/v1/result/00000000-0000-0000-0000-000000000001", "GET")]
    [InlineData("/v1/status/ws/00000000-0000-0000-0000-000000000001", "GET")]
    public async Task AsyncRoutesRequireConfiguredApiKey(string path, string method)
    {
        await using var server = await TestServer.StartAsync(apiKey: TestApiKey);
        using var response = await server.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownTaskAndMissingWebSocketUpgradeReturnClearErrors()
    {
        await using var server = await TestServer.StartAsync();
        foreach (var route in new[] { "status/poll", "result", "status/ws" })
        {
            using var response = await server.Client.GetAsync($"/v1/{route}/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        var id = await SubmitTaskAsync(server.Client);
        using var noUpgrade = await server.Client.GetAsync($"/v1/status/ws/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, noUpgrade.StatusCode);
    }

    [Fact]
    public async Task AsyncSourceDownloadRunsAfterAcceptanceAndReportsFailure()
    {
        var body = new BlockingSourceStream();
        var handler = new SourceHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        await using var server = await TestServer.StartAsync(services =>
        {
            services.Configure<SourceDownloadOptions>(options => options.TimeoutSeconds = 1);
            services.AddHttpClient(SourceDocumentService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        using var accepted = await server.Client.PostAsJsonAsync("/v1/convert/source/async", new
        {
            http_sources = new[] { new { url = "https://example.com/report.md" } }
        });
        accepted.EnsureSuccessStatusCode();
        var task = await accepted.Content.ReadFromJsonAsync<ConversionTaskStatus>();
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForTaskAsync(server.Client, task!.TaskId, "failure");
        using var result = await server.Client.GetAsync($"/v1/result/{task.TaskId}");
        Assert.Equal(HttpStatusCode.GatewayTimeout, result.StatusCode);
    }

    [Fact]
    public async Task InvalidAsyncSourceSubmissionDoesNotConsumeCapacity()
    {
        await using var server = await TestServer.StartAsync(services =>
            services.Configure<AsyncConversionOptions>(options => options.MaxStoredTasks = 1));
        using var invalid = await server.Client.PostAsJsonAsync("/v1/convert/source/async", new { file_sources = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var id = await SubmitTaskAsync(server.Client);
        await WaitForTaskAsync(server.Client, id, "success");
    }

    [Theory]
    [InlineData("not-base64", 400)]
    [InlineData("YWJjZA==", 413)]
    public async Task AsyncSourceDecodingErrorsAreAvailableFromResult(string encoded, int expectedStatus)
    {
        await using var server = await TestServer.StartAsync(services =>
            services.Configure<FlexyParse.Options.FlexyParseOptions>(options => options.MaxUploadBytes = 3));
        using var submitted = await server.Client.PostAsJsonAsync("/v1/convert/source/async", new
        {
            file_sources = new[] { new { filename = "report.md", base64_string = encoded } }
        });
        submitted.EnsureSuccessStatusCode();
        var task = await submitted.Content.ReadFromJsonAsync<ConversionTaskStatus>();
        await WaitForTaskAsync(server.Client, task!.TaskId, "failure");
        using var result = await server.Client.GetAsync($"/v1/result/{task.TaskId}");
        Assert.Equal(expectedStatus, (int)result.StatusCode);
    }

    [Fact]
    public async Task UnexpectedWorkerFailuresAreSanitizedAndDoNotStopTheQueue()
    {
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(new UnexpectedTaskFailure()));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var id = await SubmitTaskAsync(server.Client);
            await WaitForTaskAsync(server.Client, id, "failure");
            using var result = await server.Client.GetAsync($"/v1/result/{id}");
            Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
            Assert.DoesNotContain("sensitive-source-url", await result.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WebSocketConnectionLimitIsEnforced()
    {
        var converter = new TaskGateConverter();
        await using var server = await TestServer.StartAsync(services =>
        {
            services.AddSingleton<IDocumentConverter>(converter);
            services.Configure<AsyncConversionOptions>(options => options.MaxWebSocketConnections = 1);
        });
        var id = await SubmitTaskAsync(server.Client);
        await converter.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first = new ClientWebSocket();
        await first.ConnectAsync(SocketUri(server.Client, id), timeout.Token);
        Assert.Equal("started", (await ReceiveStatusAsync(first, timeout.Token)).TaskStatus);
        using var second = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => second.ConnectAsync(SocketUri(server.Client, id), timeout.Token));
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stop observing", timeout.Token);
        converter.Release.TrySetResult();
        await WaitForTaskAsync(server.Client, id, "success");
    }

    [Fact]
    public async Task OpenApiIncludesAsyncSubmissionStatusResultAndWebSocketContracts()
    {
        await using var server = await TestServer.StartAsync();
        using var response = await server.Client.GetAsync("/openapi.json");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/v1/convert/file/async", "/v1/convert/source/async" })
        {
            var operation = paths.GetProperty(path).GetProperty("post");
            Assert.True(operation.TryGetProperty("requestBody", out _));
            var status = operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("properties");
            Assert.True(status.TryGetProperty("task_id", out _));
            Assert.Equal(["pending", "started", "success", "failure"], status.GetProperty("task_status").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        }
        Assert.True(paths.GetProperty("/v1/status/poll/{task_id}").GetProperty("get").GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(paths.GetProperty("/v1/result/{task_id}").GetProperty("get").GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(paths.GetProperty("/v1/status/ws/{task_id}").GetProperty("get").GetProperty("responses").TryGetProperty("101", out _));
    }

    private static async Task<Guid> SubmitTaskAsync(HttpClient client)
    {
        using var form = Form("# Task", "task.md");
        using var response = await client.PostAsync("/v1/convert/file/async", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConversionTaskStatus>())!.TaskId;
    }

    private static async Task<ConversionTaskStatus> PollAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/v1/status/poll/{id}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConversionTaskStatus>())!;
    }

    private static async Task WaitForTaskAsync(HttpClient client, Guid id, string state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var status = await PollAsync(client, id);
            if (status.TaskStatus == state) { return; }
            Assert.DoesNotContain(status.TaskStatus, new[] { "success", "failure" });
            await Task.Delay(20, timeout.Token);
        }
    }

    private static Uri SocketUri(HttpClient client, Guid id) => new UriBuilder(client.BaseAddress!)
    {
        Scheme = "ws", Path = $"/v1/status/ws/{id}"
    }.Uri;

    private static async Task<ConversionTaskStatus> ReceiveStatusAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<ConversionTaskStatus>(message.ToArray())!;
    }

    private sealed class TaskTestClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }

    private sealed class TaskGateConverter(bool failure = false) : IDocumentConverter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? UploadDirectory { get; private set; }

        public async Task ConvertAsync(ConversionInput input, Stream destination, CancellationToken cancellationToken)
        {
            UploadDirectory = Path.GetDirectoryName(input.SourcePath);
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            if (failure) { throw new DocumentConversionException(ConversionStatus.BadRequest, "Controlled conversion failure."); }
            await destination.WriteAsync("# Done"u8.ToArray(), cancellationToken);
        }
    }

    private sealed class UnexpectedTaskFailure : IDocumentConverter
    {
        public Task ConvertAsync(ConversionInput input, Stream destination, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sensitive-source-url");
    }
}
