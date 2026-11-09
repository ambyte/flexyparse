using System.Text;
using System.Text.Json;
using FlexyParse.DependencyInjection;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace FlexyParse.Tests;

public sealed class ImageRecognitionFilterTests
{
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(23, 400, false)]
    [InlineData(400, 23, false)]
    [InlineData(79, 79, false)]
    [InlineData(80, 51, false)]
    [InlineData(128, 32, true)]
    [InlineData(80, 80, true)]
    [InlineData(400, 24, true)]
    [InlineData(24, 400, true)]
    [InlineData(int.MaxValue, int.MaxValue, true)]
    [InlineData(0, 0, true)]
    public void DefaultThresholdsAreInclusiveOrientationIndependentAndOverflowSafe(int width, int height, bool expected)
    {
        Assert.Equal(expected, ImageRecognitionFilter.IsEligible(width, height, new()));
    }

    [Fact]
    public void FilterPreservesOriginalManifestMetadataOrderAndPageReferences()
    {
        var tiny = Image(16, 16, "tiny");
        var strip = Image(400, 24, "strip");
        var large = Image(200, 100, "large");
        var index = new DocumentIndex
        {
            SourceFileName = "mixed.pdf", Metadata = new() { PageCount = 3 }, Images = [tiny, strip, large]
        };
        var filtered = ImageRecognitionFilter.Filter(index, new());
        Assert.Equal([strip, large], filtered.Images);
        Assert.Equal([tiny, strip, large], index.Images);
        Assert.Same(index.Metadata, filtered.Metadata);
        Assert.Equal(index.SourceFileName, filtered.SourceFileName);
        Assert.Same(strip.PageNumbers, filtered.Images[0].PageNumbers);
        Assert.Same(index, ImageRecognitionFilter.Filter(index, new() { Enabled = false }));
        Assert.Same(filtered, ImageRecognitionFilter.Filter(filtered, new()));
    }

    [Theory]
    [InlineData("MinShortSide", "0")]
    [InlineData("MinShortSide", "-1")]
    [InlineData("MinLongSide", "0")]
    [InlineData("MinLongSide", "23")]
    [InlineData("MinPixels", "0")]
    [InlineData("MinPixels", "-1")]
    public void InvalidThresholdsFailOptionsValidation(string key, string value)
    {
        using var provider = Provider(new(), new Dictionary<string, string?>
        {
            ["FlexyParse:ImageRecognitionFilter:" + key] = value
        });
        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value);
        Assert.Contains("ImageRecognitionFilter", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RecognitionProvider.OpenAi)]
    [InlineData(RecognitionProvider.RapidOcr)]
    [InlineData(RecognitionProvider.NemotronCPU)]
    [InlineData(RecognitionProvider.NemotronGPU)]
    public async Task TinyRasterSkipsEveryProviderBeforeApiKeyCheckAndPreservesOriginal(RecognitionProvider recognitionProvider)
    {
        var recognizer = new RecordingRecognizer();
        using var provider = Provider(recognizer, new Dictionary<string, string?>
        {
            ["FlexyParse:Recognition:Provider"] = recognitionProvider.ToString(),
            ["FlexyParse:ModelApi:RequireApiKey"] = "true"
        });
        using var output = await ConvertAsync(provider, Png(16, 16), "tiny.png");
        Assert.Equal(0, recognizer.Calls);
        AssertNoErrorBlocks(output);
    }

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Markdown)]
    [InlineData(OutputFormat.Html)]
    public async Task SkippedRasterPreservesExtractedImageForEveryWriter(OutputFormat format)
    {
        var recognizer = new RecordingRecognizer();
        using var provider = Provider(recognizer);
        using var output = await ConvertAsync(provider, Png(16, 16), "tiny.png", format);
        Assert.Equal(0, recognizer.Calls);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.NotEmpty(text);
        Assert.DoesNotContain("\"type\":\"error\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledOrLoweredThresholdsAllowTinyRasterRecognition(bool enabled, bool lowerThresholds)
    {
        var recognizer = new RecordingRecognizer();
        using var provider = Provider(recognizer, new Dictionary<string, string?>
        {
            ["FlexyParse:ImageRecognitionFilter:Enabled"] = enabled.ToString(),
            ["FlexyParse:ImageRecognitionFilter:MinShortSide"] = lowerThresholds ? "1" : "24",
            ["FlexyParse:ImageRecognitionFilter:MinLongSide"] = lowerThresholds ? "1" : "80",
            ["FlexyParse:ImageRecognitionFilter:MinPixels"] = lowerThresholds ? "1" : "4096"
        });
        using var output = await ConvertAsync(provider, Png(16, 16), "tiny.png");
        Assert.Equal(1, recognizer.Calls);
        Assert.Single(recognizer.Index!.Images);
    }

    [Fact]
    public async Task EligibleImageStillRequiresOpenAiKey()
    {
        using var provider = Provider(new(), new Dictionary<string, string?>
        {
            ["FlexyParse:ModelApi:RequireApiKey"] = "true"
        });
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() => ConvertAsync(provider, Png(400, 24), "strip.png"));
        Assert.Equal(ConversionStatus.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public async Task MixedPdfRecognizesOnlyEligibleImagesButExportsBothOriginals()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        page.AddPng(Png(16, 16), new PdfRectangle(10, 10, 26, 26));
        page.AddPng(Png(400, 24), new PdfRectangle(40, 100, 440, 124));
        var recognizer = new RecordingRecognizer();
        using var provider = Provider(recognizer);
        using var output = await ConvertAsync(provider, builder.Build(), "mixed.pdf", expectedImages: 2);
        Assert.Equal(1, recognizer.Calls);
        var recognized = Assert.Single(recognizer.Index!.Images);
        Assert.Equal((400, 24), (recognized.Width, recognized.Height));
        AssertNoErrorBlocks(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayoutCropsAreFilteredBeforeByteOrFileCallbacks(bool fileEngine)
    {
        var fixture = new RecognitionLayoutFixture();
        fixture.Options.ImageRecognitionFilter = new() { MinShortSide = 30, MinLongSide = 70, MinPixels = 2900 };
        await using var workspace = JobWorkspace.Create(Path.GetTempPath(), "crops.pdf");
        var image = await fixture.SaveImageAsync(workspace);
        var calls = 0;
        Task<string> Recognize(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return Task.FromResult("text");
        }
        var pipeline = fixture.Pipeline();
        var result = fileEngine
            ? await pipeline.RecognizeFileAsync(workspace, image, (_, _, _, token) => Recognize(token), default)
            : await pipeline.RecognizeAsync(await File.ReadAllBytesAsync(Path.Combine(workspace.Root, image.RelativePath)),
                workspace, image, (_, _, _, token) => Recognize(token), default);
        Assert.Equal(2, calls); // Padded crops are 38x78 = 2964 pixels.
        Assert.Equal("text\n\ntext", result);

        fixture.Options.ImageRecognitionFilter.MinPixels = 3000;
        calls = 0;
        result = fileEngine
            ? await pipeline.RecognizeFileAsync(workspace, image, (_, _, _, token) => Recognize(token), default)
            : await pipeline.RecognizeAsync(await File.ReadAllBytesAsync(Path.Combine(workspace.Root, image.RelativePath)),
                workspace, image, (_, _, _, token) => Recognize(token), default);
        Assert.Equal(0, calls);
        Assert.Equal("", result);
        Assert.True(File.Exists(Path.Combine(workspace.Root, image.RelativePath)));
        Assert.Empty(Directory.GetFiles(workspace.Root, "recognition-crop-*.png"));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.error*", SearchOption.AllDirectories));
    }

    private static ImageManifestEntry Image(int width, int height, string hash) => new()
    {
        Hash = hash, Width = width, Height = height, RelativePath = "images/" + hash + ".png",
        MediaType = "image/png", PageNumbers = [1, 3]
    };

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.White);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static ServiceProvider Provider(RecordingRecognizer recognizer, Dictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["FlexyParse:ModelApi:RequireApiKey"] = "false",
            ["FlexyParse:KeepResults"] = "true",
            ["FlexyParse:ImageLayout:Enabled"] = "false"
        };
        foreach (var pair in settings ?? []) values[pair.Key] = pair.Value;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSingleton<IImageRecognizer>(recognizer);
        services.AddSingleton<RecordingLogger<DocumentConversionService>>();
        services.AddSingleton<ILogger<DocumentConversionService>>(provider => provider.GetRequiredService<RecordingLogger<DocumentConversionService>>());
        return services.BuildServiceProvider();
    }

    private static async Task<MemoryStream> ConvertAsync(ServiceProvider provider, byte[] bytes, string fileName,
        OutputFormat format = OutputFormat.Json, int expectedImages = 1)
    {
        using var source = new MemoryStream(bytes);
        var destination = new MemoryStream();
        try
        {
            await provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
            {
                Content = source, FileName = fileName, Format = format
            }, destination, default);
            var root = WorkspaceRoot(provider);
            var manifest = JsonSerializer.Deserialize<List<ImageManifestEntry>>(
                await File.ReadAllTextAsync(Path.Combine(root, "images", "manifest.json")), JsonDefaults.Options);
            Assert.NotNull(manifest);
            Assert.Equal(expectedImages, manifest.Count);
            Assert.All(manifest, image => Assert.True(File.Exists(Path.Combine(root, image.RelativePath))));
            var imageBlocks = 0;
            foreach (var path in Directory.GetFiles(Path.Combine(root, "pages"), "*.json"))
            {
                using var page = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                imageBlocks += page.RootElement.GetProperty("blocks").EnumerateArray()
                    .Count(block => block.GetProperty("type").GetString() == "image");
            }
            Assert.Equal(expectedImages, imageBlocks);
            Assert.Empty(Directory.GetFiles(root, "*.error*", SearchOption.AllDirectories));
            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
        finally
        {
            var root = WorkspaceRoot(provider);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string WorkspaceRoot(ServiceProvider provider) =>
        (string)provider.GetRequiredService<RecordingLogger<DocumentConversionService>>().Entries
            .Single(entry => entry.Message.StartsWith("Preserved conversion workspace", StringComparison.Ordinal)).Properties["Directory"]!;

    private static void AssertNoErrorBlocks(MemoryStream output)
    {
        using var document = JsonDocument.Parse(output.ToArray());
        var blocks = document.RootElement.GetProperty("pages").EnumerateArray()
            .SelectMany(page => page.GetProperty("blocks").EnumerateArray()).ToArray();
        Assert.DoesNotContain(blocks, block => block.GetProperty("type").GetString() == "error");
    }

    private sealed class RecordingRecognizer : IImageRecognizer
    {
        internal int Calls { get; private set; }
        internal DocumentIndex? Index { get; private set; }

        public Task RecognizeAsync(JobWorkspace workspace, DocumentIndex index, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Index = index;
            Assert.All(index.Images, image => Assert.True(File.Exists(Path.Combine(workspace.Root, image.RelativePath))));
            return Task.CompletedTask;
        }
    }
}
