using System.Net;
using System.Text;
using FlexyParse.DependencyInjection;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FlexyParse.Tests;

public sealed class GotenbergConversionTests
{
    [Theory]
    [InlineData("report.docx", "libreoffice/convert", "report.docx")]
    [InlineData("report.XLSX", "libreoffice/convert", "report.XLSX")]
    [InlineData("report.pptx", "libreoffice/convert", "report.pptx")]
    [InlineData("report.txt", "libreoffice/convert", "report.txt")]
    [InlineData("report.HTML", "chromium/convert/html", "index.html")]
    [InlineData("report.htm", "chromium/convert/html", "index.html")]
    [InlineData("report.unknown", "libreoffice/convert", "report.unknown")]
    public async Task ConvertsOtherFormatsAndProcessesReturnedPdf(string fileName, string route, string uploadedName)
    {
        string? workspaceRoot = null;
        var calls = 0;
        using var handler = new StubHandler(async (request, token) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://gotenberg:3000/prefix/forms/" + route, request.RequestUri?.AbsoluteUri);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var part = Assert.Single(form);
            Assert.Equal("files", part.Headers.ContentDisposition?.Name?.Trim('"'));
            Assert.Equal(uploadedName, part.Headers.ContentDisposition?.FileName?.Trim('"'));
            workspaceRoot = Path.Combine(Path.GetTempPath(), "flexyparse", Assert.Single(request.Headers.GetValues("Gotenberg-Trace")));
            Assert.Equal("source document", await part.ReadAsStringAsync(token));
            return PdfResponse();
        });
        using var provider = CreateProvider(handler);
        await using var destination = new MemoryStream();
        await provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
        {
            Content = new MemoryStream("source document"u8.ToArray()),
            FileName = fileName,
            Format = OutputFormat.Markdown
        }, destination, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Contains("Converted with Gotenberg", Encoding.UTF8.GetString(destination.ToArray()), StringComparison.Ordinal);
        Assert.NotNull(workspaceRoot);
        Assert.False(Directory.Exists(workspaceRoot));
        Assert.True(destination.CanWrite);
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("report.PDF", true)]
    [InlineData("report.bin", true)]
    [InlineData("report.png", true)]
    [InlineData("report.md", false)]
    [InlineData("report.MARKDOWN", false)]
    public async Task PdfAndMarkdownDoNotCallGotenberg(string fileName, bool pdf)
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("Gotenberg must not be called."));
        using var provider = CreateProvider(handler);
        await using var source = new MemoryStream(pdf ? BuildPdf() : "# Local Markdown"u8.ToArray());
        await using var destination = new MemoryStream();
        await provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
        {
            Content = source,
            FileName = fileName,
            Format = OutputFormat.Markdown
        }, destination, CancellationToken.None);
        Assert.NotEmpty(destination.ToArray());
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyAddressPreservesRejectionOfOtherFormats(string url)
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("Gotenberg must not be called."));
        using var provider = CreateProvider(handler, url: url);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => ConvertAsync(provider));
        Assert.Equal(ConversionStatus.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task InvalidPdfIsNotSentToGotenberg()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("Gotenberg must not be called."));
        using var provider = CreateProvider(handler);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => ConvertAsync(provider, "invalid.pdf"));
        Assert.Equal(ConversionStatus.BadRequest, exception.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ConversionStatus.BadRequest)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, ConversionStatus.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ConversionStatus.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ConversionStatus.PayloadTooLarge)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ConversionStatus.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ConversionStatus.ServiceUnavailable)]
    public async Task MapsRemoteErrorsWithoutExposingResponseBody(HttpStatusCode remoteStatus, int expectedStatus)
    {
        string? workspaceRoot = null;
        using var handler = new StubHandler(async (request, token) =>
        {
            workspaceRoot = Path.Combine(Path.GetTempPath(), "flexyparse", Assert.Single(request.Headers.GetValues("Gotenberg-Trace")));
            await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(remoteStatus) { Content = new StringContent("private remote error") };
        });
        using var provider = CreateProvider(handler);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => ConvertAsync(provider));
        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.DoesNotContain("private remote error", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(workspaceRoot);
        Assert.False(Directory.Exists(workspaceRoot));
    }

    [Fact]
    public async Task RejectsInvalidGotenbergPdf()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not a PDF")
        }));
        using var provider = CreateProvider(handler);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => ConvertAsync(provider));
        Assert.Equal(ConversionStatus.ServiceUnavailable, exception.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LimitsConvertedPdfSizeWithAndWithoutContentLength(bool knownLength)
    {
        using var handler = new StubHandler((_, _) =>
        {
            var bytes = new byte[1025];
            HttpContent content = knownLength
                ? new ByteArrayContent(bytes)
                : new StreamContent(new NonSeekableStream(bytes));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var provider = CreateProvider(handler, maxBytes: 1024);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => ConvertAsync(provider));
        Assert.Equal(ConversionStatus.PayloadTooLarge, exception.StatusCode);
    }

    [Fact]
    public async Task InputSizeIsCheckedBeforeCallingGotenberg()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("Gotenberg must not be called."));
        using var provider = CreateProvider(handler, maxBytes: 1);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => ConvertAsync(provider));
        Assert.Equal(ConversionStatus.PayloadTooLarge, exception.StatusCode);
    }

    [Fact]
    public async Task ConnectionFailureReturnsServiceUnavailable()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("connection failed"));
        using var provider = CreateProvider(handler);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => ConvertAsync(provider));
        Assert.Equal(ConversionStatus.ServiceUnavailable, exception.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeoutAreDistinguished(bool timeout)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return PdfResponse();
        });
        using var provider = CreateProvider(handler, timeoutSeconds: 1);
        using var cancellation = new CancellationTokenSource();
        var conversion = ConvertAsync(provider, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (timeout)
        {
            var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => conversion);
            Assert.Equal(ConversionStatus.ServiceUnavailable, exception.StatusCode);
        }
        else
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion);
        }
    }

    [Theory]
    [InlineData("ftp://gotenberg")]
    [InlineData("not a URL")]
    [InlineData("http://user:password@gotenberg")]
    [InlineData("http://gotenberg?query=1")]
    [InlineData("http://gotenberg#fragment")]
    public void RejectsInvalidServiceAddress(string url)
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var provider = CreateProvider(handler, url: url);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertsFileFromDiskAndHonorsMoveSource(bool moveSource)
    {
        var root = Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "report.docx");
            await File.WriteAllTextAsync(source, "source document");
            using var handler = new StubHandler((_, _) => Task.FromResult(PdfResponse()));
            using var provider = CreateProvider(handler);
            await using var destination = new MemoryStream();
            await provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
            {
                SourcePath = source,
                MoveSource = moveSource,
                FileName = "report.docx",
                Format = OutputFormat.Json
            }, destination, CancellationToken.None);
            Assert.Equal(!moveSource, File.Exists(source));
            Assert.Contains("report.docx", Encoding.UTF8.GetString(destination.ToArray()), StringComparison.Ordinal);
            Assert.Contains("Converted with Gotenberg", Encoding.UTF8.GetString(destination.ToArray()), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TimeoutAlsoCoversStreamingResponseBody()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream())
        }));
        using var provider = CreateProvider(handler, timeoutSeconds: 1);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => ConvertAsync(provider));
        Assert.Equal(ConversionStatus.ServiceUnavailable, exception.StatusCode);
    }

    private static ServiceProvider CreateProvider(HttpMessageHandler handler, string url = "http://gotenberg:3000/prefix",
        long maxBytes = 5_000_000, int timeoutSeconds = 180)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:GotenbergUrl"] = url,
            ["FlexyParse:GotenbergTimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FlexyParse:MaxUploadBytes"] = maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FlexyParse:RecognizeImages"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(configuration);
        services.AddHttpClient(GotenbergPdfConverter.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static async Task ConvertAsync(ServiceProvider provider, string fileName = "report.docx", CancellationToken cancellationToken = default)
    {
        await using var content = new MemoryStream("source document"u8.ToArray());
        await using var destination = new MemoryStream();
        await provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
        {
            Content = content,
            FileName = fileName,
            Format = OutputFormat.Json
        }, destination, cancellationToken);
    }

    private static HttpResponseMessage PdfResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(BuildPdf()) };

    private static byte[] BuildPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(600, 800).AddText("Converted with Gotenberg", 12, new PdfPoint(72, 750), font);
        return builder.Build();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class BlockingStream : MemoryStream
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
