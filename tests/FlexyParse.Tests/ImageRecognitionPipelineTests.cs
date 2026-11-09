using FlexyParse.Hosting;
using FlexyParse.ImageLayout;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FlexyParse.Tests;

public sealed class ImageRecognitionPipelineTests
{
    [Theory]
    [InlineData("OpenAi")]
    [InlineData("RapidOcr")]
    [InlineData("NemotronCPU")]
    [InlineData("NemotronGPU")]
    public async Task SharedPipelineIsRegisteredForEveryProvider(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredFlexyParse(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:Recognition:Provider"] = name
        }).Build());
        await using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<ImageRecognitionPipeline>(), provider.GetRequiredService<ImageRecognitionPipeline>());
        Assert.NotNull(provider.GetRequiredService<IImageRecognizer>());
    }

    [Theory]
    [InlineData("split")]
    [InlineData("disabled")]
    [InlineData("single")]
    [InlineData("empty")]
    [InlineData("coverage")]
    [InlineData("failure")]
    [InlineData("oversized")]
    public async Task FileEnginesReceiveOrderedPngCropsOrTheUnchangedOriginal(string scenario)
    {
        var fixture = new RecognitionLayoutFixture(scenario);
        await using var workspace = JobWorkspace.Create(Path.GetTempPath(), "pipeline.pdf");
        var image = await fixture.SaveImageAsync(workspace);
        var source = Path.Combine(workspace.Root, image.RelativePath);
        var paths = new List<string>();
        var kinds = new List<string?>();
        var colors = new List<SKColor>();
        var pipeline = fixture.Pipeline();
        var result = await pipeline.RecognizeFileAsync(workspace, image, (path, mediaType, kind, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("image/png", mediaType);
            paths.Add(path);
            kinds.Add(kind);
            using var bitmap = SKBitmap.Decode(path);
            if (scenario == "split")
            {
                Assert.Equal((38, 78), (bitmap.Width, bitmap.Height));
                colors.Add(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
            }
            else
            {
                Assert.Equal(source, path);
                Assert.Equal((100, 100), (bitmap.Width, bitmap.Height));
            }
            return Task.FromResult("Part " + paths.Count);
        }, default);
        Assert.Equal(scenario == "split" ? "Part 1\n\nPart 2" : "Part 1", result);
        if (scenario == "split")
        {
            Assert.Equal(["text", "table"], kinds);
            Assert.Equal([SKColors.Black, SKColors.Blue], colors);
            Assert.All(paths, path => Assert.False(File.Exists(path)));
        }
        else Assert.Null(Assert.Single(kinds));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(workspace.Root, "recognition-crop-*.png"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileCropsAreRemovedAndPartialResultsAreNotReturnedOnFailureOrCancellation(bool cancel)
    {
        var fixture = new RecognitionLayoutFixture();
        await using var workspace = JobWorkspace.Create(Path.GetTempPath(), "pipeline.pdf");
        var image = await fixture.SaveImageAsync(workspace);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var paths = new List<string>();
        var work = fixture.Pipeline().RecognizeFileAsync(workspace, image, (path, _, _, token) =>
        {
            paths.Add(path);
            Assert.True(File.Exists(path));
            if (++calls == 2)
            {
                if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                throw new InvalidDataException("Test failure");
            }
            return Task.FromResult("Partial result");
        }, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        else await Assert.ThrowsAsync<InvalidDataException>(() => work);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.Empty(Directory.GetFiles(workspace.Root, "recognition-crop-*.png"));
    }
}

internal sealed class RecognitionLayoutFixture(string scenario = "split") : IImageLayoutDetector
{
    internal FlexyParseOptions Options { get; } = new()
    {
        ImageRecognitionFilter = new() { Enabled = false },
        ImageLayout = new() { Enabled = scenario != "disabled" },
        ModelApi = new() { MaxImageBytes = scenario == "oversized" ? 1 : 20_000_000 }
    };
    internal int Calls { get; private set; }

    internal ImageRecognitionPipeline Pipeline()
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        return new(options, new ImageLayoutProcessor(this, options, NullLogger<ImageLayoutProcessor>.Instance));
    }

    internal async Task<ImageManifestEntry> SaveImageAsync(JobWorkspace workspace)
    {
        using var bitmap = new SKBitmap(100, 100);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(10, 10, 30, 70, paint);
        paint.Color = SKColors.Blue;
        canvas.DrawRect(60, 10, 30, 70, paint);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var image = new ImageManifestEntry { Hash = "image", RelativePath = "images/image.png", MediaType = "image/png" };
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, image.RelativePath), encoded.ToArray());
        return image;
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<ImageLayoutRegion>> DetectAsync(SKBitmap image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        ImageLayoutRegion[] regions = scenario switch
        {
            "empty" => [],
            "single" => [new("Picture", new(0, 0, 100, 100), 0.9)],
            "coverage" => [new("Text", new(10, 10, 40, 30), 0.9), new("Text", new(10, 40, 40, 60), 0.9)],
            "failure" => throw new InvalidOperationException("Test detector failure"),
            _ => [new("Table", new(60, 10, 90, 80), 0.9), new("Text", new(10, 10, 40, 80), 0.9)]
        };
        return ValueTask.FromResult<IReadOnlyList<ImageLayoutRegion>>(regions);
    }
}
