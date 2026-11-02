using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FlexyParse.Options;
using FlexyParse.RapidOcr;
using FlexyParse.Recognition;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlexyParse.Tests;

public sealed class RapidOcrModelStoreTests
{
    internal static RapidOcrModelStore OfflineStore(FlexyParseOptions options, RapidOcrOptions localOptions) => new(
        new ClientFactory(new Handler((_, _) => throw new InvalidOperationException("Unexpected network access."))),
        Microsoft.Extensions.Options.Options.Create(options.Recognition), Microsoft.Extensions.Options.Options.Create(localOptions),
        NullLogger<RapidOcrModelStore>.Instance);

    [Fact]
    public async Task DownloadsOnlyRequestedProfileAndReusesVerifiedCacheOffline()
    {
        using var fixture = new Fixture();
        var paths = await fixture.Store.EnsureAvailableAsync("eslav", default);
        Assert.NotNull(paths);
        Assert.Equal(4, fixture.Requests.Count);
        Assert.Equal(4, Directory.GetFiles(fixture.Root).Length);
        Assert.DoesNotContain(fixture.Requests.Keys, name => name.Contains("latin", StringComparison.Ordinal));
        fixture.Response = (_, _) => throw new InvalidOperationException("Offline");
        Assert.Equal(paths, await fixture.Store.EnsureAvailableAsync("eslav", default));
        Assert.Equal(4, fixture.Requests.Count);
    }

    [Fact]
    public async Task CorruptionIsDetectedEvenWhenLengthIsUnchanged()
    {
        using var fixture = new Fixture();
        var paths = (await fixture.Store.EnsureAvailableAsync("eslav", default))!;
        var original = await File.ReadAllBytesAsync(paths.RecognitionModelPath);
        original[0] ^= 1;
        await File.WriteAllBytesAsync(paths.RecognitionModelPath, original);
        await fixture.Store.EnsureAvailableAsync("eslav", default);
        Assert.Equal(2, fixture.Requests["eslav.onnx"]);
        Assert.Equal(5, fixture.Requests.Values.Sum());
    }

    [Fact]
    public async Task ConcurrentProfilesReuseSharedFilesAndDoNotMixDictionaries()
    {
        using var fixture = new Fixture();
        var paths = await Task.WhenAll(Enumerable.Range(0, 18).Select(i => fixture.Store.EnsureAvailableAsync(
            i % 3 == 0 ? "eslav" : i % 3 == 1 ? "en" : "cyrillic", default)));
        Assert.Equal(8, fixture.Requests.Count);
        Assert.All(fixture.Requests.Values, count => Assert.Equal(1, count));
        Assert.EndsWith("eslav.txt", paths[0]!.DictionaryPath);
        Assert.EndsWith("en.txt", paths[1]!.DictionaryPath);
        Assert.EndsWith("cyrillic.txt", paths[2]!.DictionaryPath);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.download"));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("oversized-header")]
    [InlineData("oversized-stream")]
    [InlineData("empty")]
    [InlineData("incomplete")]
    [InlineData("http")]
    [InlineData("status")]
    public async Task InvalidDownloadsNeverPublishAndCleanTemporaryFiles(string failure)
    {
        using var fixture = new Fixture();
        fixture.Response = (request, _) =>
        {
            var response = Fixture.ValidResponse(request);
            switch (failure)
            {
                case "hash": response.Content = new ByteArrayContent("bad bytes"u8.ToArray()); break;
                case "oversized-header": response.Content.Headers.ContentLength = 1025; break;
                case "oversized-stream":
                    response.Content = new StreamContent(new NonSeekableStream(new byte[1025]));
                    response.Content.Headers.ContentLength = null;
                    break;
                case "empty": response.Content = new ByteArrayContent([]); break;
                case "incomplete": response.Content.Headers.ContentLength = 100; break;
                case "http": response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://example.test/model"); break;
                case "status": response.StatusCode = HttpStatusCode.BadGateway; break;
            }

            return Task.FromResult(response);
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.EnsureAvailableAsync("eslav", default));
        Assert.Contains("provisioning failed", exception.Message);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task FailedRepairPreservesOldCacheButNeverAcceptsIt()
    {
        using var fixture = new Fixture();
        var paths = (await fixture.Store.EnsureAvailableAsync("eslav", default))!;
        await File.WriteAllTextAsync(paths.DictionaryPath, "corrupt");
        fixture.Response = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new ByteArrayContent("wrong hash"u8.ToArray())
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.EnsureAvailableAsync("eslav", default));
        Assert.Equal("corrupt", await File.ReadAllTextAsync(paths.DictionaryPath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.download"));
    }

    [Fact]
    public async Task CancellationDuringBodyCleansTemporaryAndReleasesGate()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new StreamContent(new BlockingStream(entered))
        });
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Store.EnsureAvailableAsync("eslav", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var queuedCancellation = new CancellationTokenSource();
        var queued = fixture.Store.EnsureAvailableAsync("en", queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Directory.GetFiles(fixture.Root));
        fixture.Response = (request, _) => Task.FromResult(Fixture.ValidResponse(request));
        Assert.NotNull(await fixture.Store.EnsureAvailableAsync("eslav", default));
    }

    [Fact]
    public async Task BodyTransferTimeoutCleansTemporaryAndDoesNotPublish()
    {
        using var fixture = new Fixture(TimeSpan.FromMilliseconds(100));
        fixture.Response = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new StreamContent(new BlockingStream(new TaskCompletionSource()))
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.EnsureAvailableAsync("eslav", default));
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public void OfficialPresetsAreVersionPinnedAndShareDetectionAssets()
    {
        var eslav = RapidOcrModelPresets.Get("eslav");
        foreach (var profile in OcrLanguageProfiles.ModelNames)
        {
            var preset = RapidOcrModelPresets.Get(profile);
            Assert.Same(eslav.Detection, preset.Detection);
            Assert.Same(eslav.Classification, preset.Classification);
            foreach (var asset in new[] { preset.Detection, preset.Classification, preset.Recognition, preset.Dictionary })
            {
                Assert.StartsWith("https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/", asset.Url);
                Assert.Equal(32, Convert.FromHexString(asset.Sha256).Length);
            }
            var expectedModel = profile == "ch_server" ? "ch_PP-OCRv5_rec_server.onnx" : profile + "_PP-OCRv5_rec_mobile.onnx";
            var expectedDictionary = profile is "ch" or "ch_server" ? "ppocrv5_dict.txt" : "ppocrv5_" + profile + "_dict.txt";
            Assert.Equal(expectedModel, preset.Recognition.FileName);
            Assert.Equal(expectedDictionary, preset.Dictionary.FileName);
            Assert.EndsWith("/" + expectedModel, preset.Recognition.Url);
            Assert.EndsWith("/" + expectedModel[..^5] + "/" + expectedDictionary, preset.Dictionary.Url);
            Assert.Equal(1024 * 1024, preset.Dictionary.MaxBytes);
        }
    }

    [Fact]
    public async Task AllModelPrefixesProvisionOnlyTheirOwnRecognitionAndDictionary()
    {
        foreach (var model in OcrLanguageProfiles.ModelNames)
        {
            using var fixture = new Fixture();
            var paths = await fixture.Store.EnsureAvailableAsync(model, default);
            Assert.NotNull(paths);
            Assert.EndsWith(model + ".onnx", paths.RecognitionModelPath);
            Assert.EndsWith(model + ".txt", paths.DictionaryPath);
            Assert.Equal(4, fixture.Requests.Count);
            Assert.Equal(paths, await fixture.Store.EnsureAvailableAsync(model.ToUpperInvariant(), default));
            Assert.All(fixture.Requests.Values, count => Assert.Equal(1, count));
        }
    }

    [Fact]
    public async Task OpenAiDoesNoDiskOrNetworkWorkEvenWithUnsupportedLanguage()
    {
        using var fixture = new Fixture();
        fixture.Options.Recognition.Provider = RecognitionProvider.OpenAi;
        fixture.Response = (_, _) => throw new InvalidOperationException("Unexpected HTTP");
        Assert.Null(await fixture.Store.EnsureAvailableAsync("not-supported", default));
        Assert.Empty(fixture.Requests);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task ExplicitLocalPathsOverrideAllPresetsWithoutNetworkOrMutation()
    {
        using var fixture = new Fixture();
        var local = fixture.LocalOptions;
        var path = Path.Combine(fixture.Root, "custom");
        await File.WriteAllTextAsync(path, "custom unpinned model");
        local.DetectionModelPath = local.ClassificationModelPath = path;
        local.Profiles["eslav"] = new() { RecognitionModelPath = path, DictionaryPath = path };
        var paths = await fixture.Store.EnsureAvailableAsync("eslav", default);
        Assert.Equal(new RapidOcrModelPaths(path, path, path, path), paths);
        Assert.Empty(fixture.Requests);
        Assert.False(local.Profiles.ContainsKey("latin"));
    }

    [Fact]
    public async Task UnsupportedLanguageAndPreCancelledCallsDoNotDownload()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.EnsureAvailableAsync("de", default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.EnsureAvailableAsync("eslav", cancellation.Token));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task DownloadsReportProgressAndVerificationWithoutClaimingSuccessOnCorruption()
    {
        using var fixture = new Fixture();
        await fixture.Store.EnsureAvailableAsync("eslav", default);
        var entries = fixture.Logger.Entries.ToArray();
        var progress = entries.Where(entry => entry.Properties.ContainsKey("DownloadPercent")).ToArray();
        Assert.Equal(8, progress.Length); // Four small files each report initial and final progress.
        Assert.Equal(4, progress.Count(entry => Equals(entry.Properties["DownloadPercent"], 0d)));
        Assert.Equal(4, progress.Count(entry => Equals(entry.Properties["DownloadPercent"], 100d)));
        Assert.Equal(4, entries.Count(entry => entry.Message.Contains("Verifying SHA-256", StringComparison.Ordinal)));
        Assert.Equal(4, entries.Count(entry => entry.Message.Contains("downloaded and verified", StringComparison.Ordinal)));

        using var corrupt = new Fixture();
        corrupt.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("corrupt")) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => corrupt.Store.EnsureAvailableAsync("eslav", default));
        Assert.DoesNotContain(corrupt.Logger.Entries, entry => entry.Message.Contains("downloaded and verified", StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rapidocr-store-" + Guid.NewGuid().ToString("N"));
        public FlexyParseOptions Options { get; } = new();
        public RapidOcrOptions LocalOptions { get; } = new();
        public ConcurrentDictionary<string, int> Requests { get; } = new();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Response { get; set; } =
            (request, _) => Task.FromResult(ValidResponse(request));
        public RapidOcrModelStore Store { get; }
        public RecordingLogger<RapidOcrModelStore> Logger { get; } = new();

        public Fixture(TimeSpan? downloadTimeout = null)
        {
            Directory.CreateDirectory(Root);
            Options.Recognition.Provider = RecognitionProvider.RapidOcr;
            LocalOptions.ModelsDirectory = Root;
            Store = new RapidOcrModelStore(new ClientFactory(new Handler((request, token) =>
            {
                Requests.AddOrUpdate(Path.GetFileName(request.RequestUri!.AbsolutePath), 1, (_, count) => count + 1);
                return Response(request, token);
            })), Microsoft.Extensions.Options.Options.Create(Options.Recognition), Microsoft.Extensions.Options.Options.Create(LocalOptions),
                Logger,
                profile => new(Asset("det.onnx"), Asset("cls.onnx"), Asset(profile + ".onnx"), Asset(profile + ".txt")), downloadTimeout);
        }

        private static RapidOcrModelAsset Asset(string name) => new(name, "https://example.test/" + name,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))), 1024);

        internal static HttpResponseMessage ValidResponse(HttpRequestMessage request) => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Path.GetFileName(request.RequestUri!.AbsolutePath)))
        };

        public void Dispose()
        {
            Store.Dispose();
            Directory.Delete(Root, true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class BlockingStream(TaskCompletionSource entered) : MemoryStream
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
