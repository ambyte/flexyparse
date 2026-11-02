using System.Text;
using FlexyParse.DependencyInjection;
using FlexyParse.Extraction;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FlexyParse.Tests;

public class ConversionCancellationTests
{
    [Fact]
    public async Task AlreadyCancelledConversionDoesNotReadInput()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var input = new BlockingStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRequiredService<IDocumentConverter>()
            .ConvertAsync(new ConversionInput { Content = input, FileName = "test.md", Format = OutputFormat.Json }, Stream.Null, cancellation.Token));
        Assert.False(input.Entered.Task.IsCompleted);
    }

    [Fact]
    public async Task CancelsWhileWaitingForDocumentSlotWithoutReadingInput()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var lease = await provider.GetRequiredService<DocumentProcessingGate>().EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await using var input = new BlockingStream();
        var task = provider.GetRequiredService<IDocumentConverter>().ConvertAsync(
            new ConversionInput { Content = input, FileName = "queued.md", Format = OutputFormat.Json }, Stream.Null, cancellation.Token);
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(input.Entered.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelsInputOrOutputAndReleasesWorkspaceAndSlot(bool cancelOutput)
    {
        var logger = new RecordingLogger<DocumentConversionService>();
        var services = CreateServices();
        services.AddSingleton<ILogger<DocumentConversionService>>(logger);
        await using var provider = services.BuildServiceProvider();
        var converter = provider.GetRequiredService<IDocumentConverter>();
        using var cancellation = new CancellationTokenSource();
        await using var blocking = new BlockingStream();
        await using var markdown = new MemoryStream("# Test"u8.ToArray());
        var task = converter.ConvertAsync(new ConversionInput
        {
            Content = cancelOutput ? markdown : blocking,
            FileName = "cancel.md",
            Format = OutputFormat.Markdown
        }, cancelOutput ? blocking : Stream.Null, cancellation.Token);
        try
        {
            await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
            var entry = Assert.Single(logger.Entries, entry => entry.Message.StartsWith("Conversion cancelled", StringComparison.Ordinal));
            var directory = Path.Combine(new FlexyParseOptions().ResolveTempDirectory(), (string)entry.Properties["JobId"]!);
            Assert.False(Directory.Exists(directory));
            await AssertCanConvertAgainAsync(converter);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task CancellationDuringPdfExtractionIsNotConvertedToPageError()
    {
        using var cancellation = new CancellationTokenSource();
        var logger = new CancellingPageLogger(cancellation);
        var services = CreateServices();
        services.AddSingleton<ILogger<DocumentExtractor>>(logger);
        await using var provider = services.BuildServiceProvider();
        var converter = provider.GetRequiredService<IDocumentConverter>();
        await using var input = new MemoryStream(BuildPdf(withImage: false));
        await using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(
            new ConversionInput { Content = input, FileName = "cancel.pdf", Format = OutputFormat.Json }, output, cancellation.Token));
        Assert.NotNull(logger.JobId);
        Assert.False(Directory.Exists(Path.Combine(new FlexyParseOptions().ResolveTempDirectory(), logger.JobId)));
        Assert.Equal(0, output.Length);
        await AssertCanConvertAgainAsync(converter);
    }

    [Fact]
    public async Task CancellationReachesModelHttpRequestAndCleansWorkspace()
    {
        var handler = new BlockingModelHandler();
        var logger = new RecordingLogger<DocumentConversionService>();
        var services = CreateServices();
        services.Configure<FlexyParseOptions>(options =>
        {
            options.RecognizeImages = true;
            options.ModelApi.RequireApiKey = false;
        });
        services.AddSingleton<ILogger<DocumentConversionService>>(logger);
        services.AddHttpClient(OpenAiImageRecognizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var converter = provider.GetRequiredService<IDocumentConverter>();
        using var cancellation = new CancellationTokenSource();
        await using var input = new MemoryStream(BuildPdf(withImage: true));
        var task = converter.ConvertAsync(new ConversionInput { Content = input, FileName = "image.pdf", Format = OutputFormat.Json }, Stream.Null, cancellation.Token);
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(handler.Cancelled);
            var entry = Assert.Single(logger.Entries, entry => entry.Message.StartsWith("Conversion cancelled", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(new FlexyParseOptions().ResolveTempDirectory(), (string)entry.Properties["JobId"]!)));
            await AssertCanConvertAgainAsync(converter);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(new ConfigurationBuilder().Build());
        services.Configure<FlexyParseOptions>(options =>
        {
            options.MaxConcurrentDocuments = 1;
            options.RecognizeImages = false;
        });
        return services;
    }

    private static async Task AssertCanConvertAgainAsync(IDocumentConverter converter)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var input = new MemoryStream("# Retry"u8.ToArray());
        await using var output = new MemoryStream();
        await converter.ConvertAsync(new ConversionInput
        {
            Content = input, FileName = "retry.md", Format = OutputFormat.Markdown
        }, output, timeout.Token);
        Assert.Equal("# Retry", Encoding.UTF8.GetString(output.ToArray()));
    }

    internal static byte[] BuildPdf(bool withImage)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("Cancellation test", 12, new PdfPoint(72, 760), font);
        if (withImage)
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAoAAAAKCAIAAAACUFjqAAAAE0lEQVR4nGP4z8CAB+GT+z9ypQG3ymOdJh6tQAAAAABJRU5ErkJggg==");
            page.AddPng(png, new PdfRectangle(72, 500, 172, 600));
        }

        return builder.Build();
    }

    private sealed class BlockingStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    internal sealed class BlockingModelHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage();
            }
            catch (OperationCanceledException)
            {
                Cancelled = cancellationToken.IsCancellationRequested;
                throw;
            }
        }
    }

    private sealed class CancellingPageLogger(CancellationTokenSource cancellation) : ILogger<DocumentExtractor>
    {
        public string? JobId { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Processing page", StringComparison.Ordinal))
            {
                JobId = (string)((IEnumerable<KeyValuePair<string, object?>>)state!).Single(pair => pair.Key == "JobId").Value!;
                cancellation.Cancel();
            }
        }
    }
}
