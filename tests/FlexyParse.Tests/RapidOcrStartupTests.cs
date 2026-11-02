using System.Net;
using System.Security.Cryptography;
using System.Text;
using FlexyParse.Options;
using FlexyParse.RapidOcr;
using FlexyParse.Recognition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GenericHost = Microsoft.Extensions.Hosting.Host;

namespace FlexyParse.Tests;

public sealed class RapidOcrStartupTests
{
    [Fact]
    public async Task OpenAiStartupDoesNotAccessNetworkDiskOrRecognizer()
    {
        using var fixture = new Fixture();
        fixture.Options.Recognition.Provider = RecognitionProvider.OpenAi;
        fixture.Options.Recognition.Language = "unsupported-by-local-ocr";
        fixture.Send = (_, _) => throw new InvalidOperationException("Unexpected download.");
        var builder = GenericHost.CreateApplicationBuilder();
        fixture.Register(builder.Services);
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.Empty(fixture.Profiles);
        Assert.Equal(0, fixture.Requests);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData("eslav", "eslav")]
    [InlineData("en", "en")]
    [InlineData("cyrillic", "cyrillic")]
    public async Task StartupProvisionsOnlyConfiguredDefaultProfile(string language, string profile)
    {
        using var fixture = new Fixture();
        fixture.Options.Recognition.Language = language;
        var builder = GenericHost.CreateApplicationBuilder();
        fixture.Register(builder.Services);
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.Equal(profile, Assert.Single(fixture.Profiles));
        Assert.Equal(4, fixture.Requests);
        Assert.Equal(4, Directory.GetFiles(fixture.Root).Length);
        Assert.True(File.Exists(Path.Combine(fixture.Root, profile + ".onnx")));
        Assert.Empty(fixture.LocalOptions.GetProfile(profile).RecognitionModelPath);
    }

    [Fact]
    public async Task WebServerDoesNotListenUntilDefaultModelsAreReady()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Send = async (request, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Fixture.Response(request);
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        fixture.Register(builder.Services);
        await using var app = builder.Build();
        var starting = app.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(starting.IsCompleted);
            Assert.Empty(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        }
        finally
        {
            release.TrySetResult();
            await starting;
        }

        Assert.NotEmpty(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        await app.StopAsync();
    }

    [Fact]
    public async Task ProvisioningFailurePropagatesAndPreventsHostStartup()
    {
        using var fixture = new Fixture();
        var failure = new HttpRequestException("Offline test transport.");
        fixture.Send = (_, _) => throw failure;
        var builder = GenericHost.CreateApplicationBuilder();
        fixture.Register(builder.Services);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Same(failure, exception.InnerException);
        Assert.Contains("provisioning failed", exception.Message);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
    }

    [Fact]
    public async Task StartupCancellationIsForwardedToProvisioning()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Send = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable.");
        };
        var builder = GenericHost.CreateApplicationBuilder();
        fixture.Register(builder.Services);
        using var host = builder.Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var starting = host.StartAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rapidocr-startup-" + Guid.NewGuid().ToString("N"));
        public FlexyParseOptions Options { get; } = new();
        public RapidOcrOptions LocalOptions { get; } = new();
        public List<string> Profiles { get; } = [];
        public int Requests { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; set; } =
            (request, _) => Task.FromResult(Response(request));
        private readonly Handler _handler;

        public Fixture()
        {
            Options.Recognition.Provider = RecognitionProvider.RapidOcr;
            LocalOptions.ModelsDirectory = Root;
            _handler = new Handler((request, token) =>
            {
                Requests++;
                return Send(request, token);
            });
        }

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<IOptions<RecognitionOptions>>(Microsoft.Extensions.Options.Options.Create(Options.Recognition));
            services.AddSingleton(_ => new RapidOcrModelStore(new ClientFactory(_handler),
                Microsoft.Extensions.Options.Options.Create(Options.Recognition), Microsoft.Extensions.Options.Options.Create(LocalOptions),
                NullLogger<RapidOcrModelStore>.Instance, profile =>
                {
                    Profiles.Add(profile);
                    return new(Asset("det.onnx"), Asset("cls.onnx"), Asset(profile + ".onnx"), Asset(profile + ".txt"));
                }));
            services.AddSingleton<IImageRecognizer>(_ => throw new InvalidOperationException("Startup must not resolve the recognizer."));
            services.AddHostedService<RapidOcrStartupService>();
        }

        private static RapidOcrModelAsset Asset(string name) => new(name, "https://example.test/" + name,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))), 1024);

        public static HttpResponseMessage Response(HttpRequestMessage request) => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Path.GetFileName(request.RequestUri!.AbsolutePath)))
        };

        public void Dispose()
        {
            _handler.Dispose();
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(RapidOcrModelStore.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
