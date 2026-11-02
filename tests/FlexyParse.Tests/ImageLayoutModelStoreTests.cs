using System.Net;
using System.Security.Cryptography;
using FlexyParse.Hosting;
using FlexyParse.ImageLayout;
using FlexyParse.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace FlexyParse.Tests;

public sealed class ImageLayoutModelStoreTests : IDisposable
{
    private static readonly byte[] ModelBytes = "deterministic test model bytes"u8.ToArray();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flexyparse-model-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadsMissingOrCorruptModelAndReusesVerifiedCache(bool corrupt)
    {
        var settings = Settings();
        if (corrupt)
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(settings.ImageLayout.ModelPath, "corrupt");
        }

        using var handler = new Handler(() => Response());
        using var store = Store(settings, handler);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => store.EnsureAvailableAsync(CancellationToken.None)));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(new Uri(settings.ImageLayout.ModelDownloadUrl), handler.RequestUri);
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(settings.ImageLayout.ModelPath));
        Assert.Empty(Directory.GetFiles(_root, "*.download"));
        await store.EnsureAvailableAsync(CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DisabledStartupDoesNotDownloadCreateDirectoriesOrInitializeDetector()
    {
        var settings = Settings();
        settings.ImageLayout.Enabled = false;
        using var handler = new Handler(() => throw new InvalidOperationException("Unexpected network request"));
        using var store = Store(settings, handler);
        var detector = new Detector();
        var service = Startup(store, detector, settings);

        await service.StartAsync(CancellationToken.None);
        await store.EnsureAvailableAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, detector.Initializations);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task StartupInitializesDetectorOnlyAfterVerifiedFileIsAvailable()
    {
        var settings = Settings();
        using var handler = new Handler(() => Response());
        using var store = Store(settings, handler);
        var detector = new Detector(() => Assert.Equal(ModelBytes, File.ReadAllBytes(settings.ImageLayout.ModelPath)));

        await Startup(store, detector, settings).StartAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, detector.Initializations);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("empty")]
    [InlineData("length")]
    [InlineData("oversized")]
    [InlineData("http")]
    public async Task RejectsFailedDownloadsAndDoesNotInitializeOrCommit(string failure)
    {
        var settings = Settings();
        using var handler = new Handler(() =>
        {
            var response = Response();
            switch (failure)
            {
                case "checksum": response.Content = new ByteArrayContent([1, 2, 3]); break;
                case "empty": response.Content = new ByteArrayContent([]); break;
                case "length": response.Content.Headers.ContentLength = ModelBytes.Length + 1; break;
                case "oversized": response.Content.Headers.ContentLength = ImageLayoutModelStore.MaxDownloadBytes + 1; break;
                case "http": response.StatusCode = HttpStatusCode.NotFound; break;
            }

            return response;
        });
        using var store = Store(settings, handler);
        var detector = new Detector();

        await Assert.ThrowsAnyAsync<Exception>(() => Startup(store, detector, settings).StartAsync(CancellationToken.None));

        Assert.Equal(0, detector.Initializations);
        Assert.False(File.Exists(settings.ImageLayout.ModelPath));
        Assert.Empty(Directory.GetFiles(_root, "*.download"));
    }

    [Fact]
    public async Task FailedReplacementPreservesExistingFile()
    {
        var settings = Settings();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(settings.ImageLayout.ModelPath, "old file");
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        using var store = Store(settings, handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureAvailableAsync(CancellationToken.None));

        Assert.Equal("old file", await File.ReadAllTextAsync(settings.ImageLayout.ModelPath));
        Assert.Empty(Directory.GetFiles(_root, "*.download"));
    }

    [Fact]
    public async Task CancelledStreamRemovesPartialDownloadAndAllowsRetry()
    {
        var settings = Settings();
        using var cancellation = new CancellationTokenSource();
        var cancelStream = true;
        using var handler = new Handler(() => cancelStream
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelStream(cancellation)) }
            : Response());
        using var store = Store(settings, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnsureAvailableAsync(cancellation.Token));
        Assert.False(File.Exists(settings.ImageLayout.ModelPath));
        Assert.Empty(Directory.GetFiles(_root, "*.download"));

        cancelStream = false;
        await store.EnsureAvailableAsync(CancellationToken.None);
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(settings.ImageLayout.ModelPath));
    }

    [Fact]
    public async Task DisabledDetectorInitializationDoesNotLoadNativeModel()
    {
        var settings = Settings();
        settings.ImageLayout.Enabled = false;
        using var detector = new YoloImageLayoutDetector(Microsoft.Extensions.Options.Options.Create(settings));
        await detector.InitializeAsync(CancellationToken.None);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("http://example.com/model.onnx", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://user:password@example.com/model.onnx", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://example.com/model.onnx", "not-a-sha256")]
    public void RejectsUnsafeDownloadPresets(string url, string checksum)
    {
        var settings = Settings();
        settings.ImageLayout.ModelDownloadUrl = url;
        settings.ImageLayout.ModelSha256 = checksum;
        Assert.False(settings.ImageLayout.IsValid());
    }

    private FlexyParseOptions Settings() => new()
    {
        ImageLayout = new()
        {
            Enabled = true,
            ModelPath = Path.Combine(_root, "model.onnx"),
            ModelDownloadUrl = "https://example.com/pinned-revision/model.onnx",
            ModelSha256 = Convert.ToHexString(SHA256.HashData(ModelBytes))
        }
    };

    private static HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(ModelBytes) };

    private static ImageLayoutModelStore Store(FlexyParseOptions settings, Handler handler) => new(
        new ClientFactory(handler), Microsoft.Extensions.Options.Options.Create(settings), NullLogger<ImageLayoutModelStore>.Instance);

    private static ImageLayoutStartupService Startup(ImageLayoutModelStore store, Detector detector, FlexyParseOptions settings) =>
        new(store, detector, Microsoft.Extensions.Options.Options.Create(settings), NullLogger<ImageLayoutStartupService>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUri = request.RequestUri;
            return Task.FromResult(response());
        }
    }

    private sealed class ClientFactory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Detector(Action? initialize = null) : IImageLayoutDetector
    {
        internal int Initializations { get; private set; }
        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            Initializations++;
            initialize?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<ImageLayoutRegion>> DetectAsync(SKBitmap image, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CancelStream(CancellationTokenSource cancellation) : MemoryStream(ModelBytes)
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _read = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
