using System.Net;
using System.Text;
using System.Text.Json;
using FlexyParse.DependencyInjection;
using FlexyParse.ImageLayout;
using FlexyParse.Markdown;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using YoloDotNet.Models;

namespace FlexyParse.Tests;

public sealed class ImageLayoutTests
{
    [Fact]
    public void LayoutIsEnabledByDefaultWithThePresetModelAndParameters()
    {
        var options = new ImageLayoutOptions();

        Assert.True(options.Enabled);
        Assert.True(options.IsValid());
        Assert.Equal("models/yolov10m-doclaynet.onnx", options.ModelPath);
        Assert.Equal(0.35, options.Confidence);
        Assert.Equal(0.5, options.IoU);
        Assert.Equal("Proportional", options.ImageResize);
        Assert.Equal(64, options.MaxRegions);
        Assert.Equal(32_000_000, options.MaxPixels);
        Assert.Equal(4, options.PaddingPixels);
        Assert.Equal(0.98, options.MinContentCoverage);
        Assert.Equal(4, options.MaxOverlapPixels);
        Assert.Equal(0.05, options.MaxOverlapRatio);
        Assert.True(options.ResolveNestedRegions);
    }

    [Fact]
    public void ConfigurationCanExplicitlyDisableLayout()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:ImageLayout:Enabled"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddFlexyParse(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value.ImageLayout.Enabled);
    }

    private static ImageLayoutRegion Region(string label, int left, int top, int right, int bottom) =>
        new(label, new SKRectI(left, top, right, bottom), 0.9);

    [Fact]
    public void RepairsLexicographicallyOrderedModelMetadataByNumericClassId()
    {
        string[] canonical = ["Caption", "Footnote", "Formula", "List-item", "Page-footer", "Page-header",
            "Picture", "Section-header", "Table", "Text", "Title"];
        var labels = new[] { 0, 1, 10, 2, 3, 4, 5, 6, 7, 8, 9 }
            .Select((id, index) => new LabelModel { Index = index, Name = canonical[id] }).ToArray();
        const string metadata = "{0: 'Caption', 1: 'Footnote', 10: 'Title', 2: 'Formula', 3: 'List-item', 4: 'Page-footer', 5: 'Page-header', 6: 'Picture', 7: 'Section-header', 8: 'Table', 9: 'Text'}";

        YoloImageLayoutDetector.NormalizeLabels(labels, metadata);

        Assert.Equal(canonical, labels.Select(label => label.Name));
        Assert.Equal(Enumerable.Range(0, canonical.Length), labels.Select(label => label.Index));
        Assert.Equal("Picture", labels[6].Name);
        Assert.Equal("Text", labels[9].Name);
        Assert.Equal("Title", labels[10].Name);
    }

    [Fact]
    public void SupportsJsonClassMetadata()
    {
        LabelModel[] labels = [new() { Index = 0, Name = "Table" }, new() { Index = 1, Name = "Text" }];
        YoloImageLayoutDetector.NormalizeLabels(labels, "{\"1\": \"Text\", \"0\": \"Table\"}");
        Assert.Equal("Table", labels[0].Name);
        Assert.Equal("Text", labels[1].Name);
    }

    [Theory]
    [InlineData("{0: 'Text', 0: 'Table'}")]
    [InlineData("{0: 'Text', 2: 'Table'}")]
    [InlineData("{-1: 'Text', 1: 'Table'}")]
    [InlineData("{0: 'Text'}")]
    public void RejectsInvalidClassMetadataWithoutMutatingLabels(string metadata)
    {
        LabelModel[] labels = [new() { Index = 0, Name = "original" }, new() { Index = 1, Name = "original" }];
        Assert.Throws<InvalidDataException>(() => YoloImageLayoutDetector.NormalizeLabels(labels, metadata));
        Assert.All(labels, label => Assert.Equal("original", label.Name));
    }

    [Fact]
    public void ClampsMinorModelEdgeOvershootButRejectsCompletelyOutsideRegions()
    {
        var plan = ImageLayoutPlanner.Plan([Region("Picture", -1, -1, 101, 101)], 100, 100, new(), out var reason);
        Assert.Null(reason);
        Assert.Equal(new SKRectI(0, 0, 100, 100), Assert.Single(plan!).Bounds);
        Assert.Null(ImageLayoutPlanner.Plan([Region("Picture", -20, 0, 40, 40)], 100, 100, new(), out reason));
        Assert.Contains("bounds", reason);
        Assert.Null(ImageLayoutPlanner.Plan([Region("Picture", 100, 0, 101, 40)], 100, 100, new(), out reason));
        Assert.Contains("intersect", reason);
    }

    [Fact]
    public void KeepsBestClassForTheSamePhysicalBoxAndExplainsDistinctOverlaps()
    {
        var text = Region("Text", 0, 0, 40, 40);
        var competing = text with { Label = "Table", Confidence = 0.5 };
        var plan = ImageLayoutPlanner.Plan([competing, text], 100, 100, new());
        Assert.Equal(text, Assert.Single(plan!));

        Assert.Null(ImageLayoutPlanner.Plan([text, Region("Table", 20, 20, 80, 80)], 100, 100, new(), out var reason));
        Assert.Equal("Distinct regions overlap", reason);
    }

    [Fact]
    public void OrdersColumnsWithinBandsAroundSpanningBlocks()
    {
        var title = Region("Title", 0, 0, 200, 10);
        var left1 = Region("Text", 0, 20, 90, 50);
        var left2 = Region("Text", 0, 60, 90, 90);
        var right1 = Region("Text", 110, 20, 200, 50);
        var right2 = Region("Text", 110, 60, 200, 90);
        var table = Region("Table", 0, 100, 200, 120);
        var left3 = Region("Text", 0, 130, 90, 190);
        var right3 = Region("Text", 110, 130, 200, 190);

        var plan = ImageLayoutPlanner.Plan([right3, right2, left2, table, title, left3, right1, left1], 200, 200, new());

        Assert.Equal([title, left1, left2, right1, right2, table, left3, right3], plan!);
    }

    [Theory]
    [InlineData("Text", "text")]
    [InlineData("Section-header", "text")]
    [InlineData("list_item", "text")]
    [InlineData("Page Footer", "text")]
    [InlineData("Table", "table")]
    [InlineData("Picture", "figure")]
    [InlineData("Formula", "formula")]
    [InlineData("person", null)]
    public void RecognizesDocumentLabelsOnly(string label, string? expected) =>
        Assert.Equal(expected, ImageLayoutPlanner.RegionKind(label));

    [Fact]
    public void RejectsInvalidIncompleteAndAmbiguousLayouts()
    {
        var valid = Region("Text", 0, 0, 40, 40);
        Assert.Null(ImageLayoutPlanner.Plan([], 100, 100, new()));
        Assert.Null(ImageLayoutPlanner.Plan([valid, valid], 100, 100, new() { MaxRegions = 1 }));
        Assert.Null(ImageLayoutPlanner.Plan([Region("Text", -20, 0, 40, 40)], 100, 100, new()));
        Assert.Null(ImageLayoutPlanner.Plan([Region("Text", 0, 0, 120, 40)], 100, 100, new()));
        Assert.Null(ImageLayoutPlanner.Plan([Region("Text", 1, 1, 1, 40)], 100, 100, new()));
        Assert.Null(ImageLayoutPlanner.Plan([valid with { Confidence = double.NaN }], 100, 100, new()));
        Assert.Null(ImageLayoutPlanner.Plan([valid with { Confidence = 0.1 }], 100, 100, new()));
        Assert.Null(ImageLayoutPlanner.Plan([valid with { Label = "person" }], 100, 100, new()));
        Assert.Null(ImageLayoutPlanner.Plan([valid, Region("Table", 20, 20, 80, 80)], 100, 100, new()));
        Assert.Single(ImageLayoutPlanner.Plan([valid, valid with { Confidence = 0.8 }], 100, 100, new())!);
    }

    [Fact]
    public void PaddingIsClampedAndDoesNotIncludeNeighboringText()
    {
        var first = Region("Text", 0, 0, 40, 40);
        var second = Region("Text", 42, 0, 80, 40);
        Assert.Equal(first.Bounds, ImageLayoutPlanner.CropBounds(first, [first, second], 100, 100, 4));
        Assert.Equal(new SKRectI(0, 0, 44, 44), ImageLayoutPlanner.CropBounds(first, [first], 100, 100, 4));
    }

    [Fact]
    public void ChecksVisibleContentCoverageInsteadOfPageArea()
    {
        using var bitmap = TestImage();
        var left = Region("Text", 10, 10, 40, 80);
        var right = Region("Table", 60, 10, 90, 80);
        Assert.False(ImageLayoutPlanner.HasContentCoverage(bitmap, [left], new(), CancellationToken.None));
        Assert.True(ImageLayoutPlanner.HasContentCoverage(bitmap, [left, right], new(), CancellationToken.None));
        bitmap.Erase(SKColors.White);
        Assert.False(ImageLayoutPlanner.HasContentCoverage(bitmap, [left, right], new(), CancellationToken.None));
    }

    [Theory]
    [InlineData("Enabled", "true")]
    [InlineData("Confidence", "NaN")]
    [InlineData("IoU", "0")]
    [InlineData("ImageResize", "Invalid")]
    [InlineData("MaxRegions", "257")]
    [InlineData("MaxPixels", "64000001")]
    [InlineData("PaddingPixels", "-1")]
    [InlineData("MinContentCoverage", "1.1")]
    [InlineData("MaxOverlapPixels", "-1")]
    [InlineData("MaxOverlapPixels", "33")]
    [InlineData("MaxOverlapRatio", "NaN")]
    [InlineData("MaxOverlapRatio", "-0.01")]
    [InlineData("MaxOverlapRatio", "0.21")]
    public void ValidatesEnabledLayoutSettings(string property, string value)
    {
        var values = new Dictionary<string, string?>
        {
            ["FlexyParse:ImageLayout:Enabled"] = "true",
            ["FlexyParse:ImageLayout:ModelPath"] = property == "Enabled" ? "" : "layout.onnx",
            ["FlexyParse:ImageLayout:" + property] = value
        };
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var services = new ServiceCollection().AddFlexyParse(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<FlexyParseOptions>>().Value);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("empty")]
    [InlineData("failure")]
    [InlineData("coverage")]
    [InlineData("pixels")]
    [InlineData("decode")]
    [InlineData("missing-model")]
    public async Task FallsBackToWholeImage(string scenario)
    {
        using var bitmap = TestImage();
        var detector = new FakeDetector((_, _) => scenario switch
        {
            "failure" => throw new InvalidOperationException("Test inference failure"),
            "coverage" => [Region("Text", 10, 10, 40, 80)],
            _ => []
        });
        var settings = Settings();
        settings.ImageLayout.Enabled = scenario != "disabled";
        settings.ImageLayout.MaxPixels = scenario == "pixels" ? 1 : 32_000_000;
        using var yolo = new YoloImageLayoutDetector(Microsoft.Extensions.Options.Options.Create(settings));
        var processor = Processor(scenario == "missing-model" ? yolo : detector, settings);
        using var plan = await processor.PrepareAsync(scenario == "decode" ? [1, 2, 3] : Encode(bitmap), CancellationToken.None);
        Assert.Null(plan);
        if (scenario is "disabled" or "pixels" or "decode")
        {
            Assert.Equal(0, detector.Calls);
        }
    }

    [Fact]
    public async Task PreservesCancellationDuringDetection()
    {
        using var bitmap = TestImage();
        using var source = new CancellationTokenSource();
        var detector = new FakeDetector((_, token) =>
        {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            return [];
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Processor(detector, Settings()).PrepareAsync(Encode(bitmap), source.Token));
    }

    [Fact]
    public async Task SingleDetectedRegionPreservesTheOriginalImageInsteadOfCroppingAwayContent()
    {
        using var bitmap = TestImage();
        var detector = new FakeDetector((_, _) => [Region("Picture", 10, 10, 40, 80)]);
        using var plan = await Processor(detector, Settings()).PrepareAsync(Encode(bitmap), CancellationToken.None);
        Assert.Null(plan);
        Assert.Equal(1, detector.Calls);
    }

    [Fact]
    public async Task EmptyLayoutIsInformationalAndIncludesTheImageIdentityAndDimensions()
    {
        using var bitmap = TestImage();
        var logger = new RecordingLogger<ImageLayoutProcessor>();
        var detector = new FakeDetector((_, _) => []);
        var processor = new ImageLayoutProcessor(detector, Microsoft.Extensions.Options.Options.Create(Settings()), logger);
        var image = new ImageManifestEntry { Hash = "example-hash", RelativePath = "images/example.png", MediaType = "image/png", PageNumbers = [3] };

        using var plan = await processor.PrepareAsync(Encode(bitmap), CancellationToken.None, image);

        Assert.Null(plan);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("example-hash", entry.Properties["ImageHash"]);
        Assert.Equal(100, entry.Properties["Width"]);
        Assert.Equal(100, entry.Properties["Height"]);
        Assert.Equal(0, entry.Properties["DetectionCount"]);
        Assert.Equal("No document regions detected", entry.Properties["Reason"]);
    }

    [Fact]
    public async Task IncompleteMultiRegionLayoutReportsMeasuredAndRequiredCoverage()
    {
        using var bitmap = TestImage();
        var logger = new RecordingLogger<ImageLayoutProcessor>();
        var detector = new FakeDetector((_, _) => [Region("Text", 10, 10, 40, 80), Region("Text", 10, 85, 40, 95)]);
        var processor = new ImageLayoutProcessor(detector, Microsoft.Extensions.Options.Options.Create(Settings()), logger);

        using var plan = await processor.PrepareAsync(Encode(bitmap), CancellationToken.None);

        Assert.Null(plan);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(0.5, Assert.IsType<double>(entry.Properties["Coverage"]));
        Assert.Equal(0.98, Assert.IsType<double>(entry.Properties["RequiredCoverage"]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RecognizesOrderedPngCropsAndNeverCommitsPartialResults(bool fallback, bool failSecondRegion)
    {
        var root = Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
        var workspace = JobWorkspace.Create(root, "layout.pdf");
        try
        {
            using var bitmap = TestImage();
            await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "images", "layout.png"), Encode(bitmap));
            var detector = new FakeDetector((_, _) => fallback ? [] :
                [Region("Table", 60, 10, 90, 80), Region("Text", 10, 10, 40, 80)]);
            var settings = Settings();
            var handler = new CropHandler(failSecondRegion);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/v1/") };
            var recognizer = new OpenAiImageRecognizer(new ClientFactory(client),
                Microsoft.Extensions.Options.Options.Create(settings), NullLogger<OpenAiImageRecognizer>.Instance,
                new MarkdownJsonParser(), Processor(detector, settings));
            var index = new DocumentIndex
            {
                SourceFileName = "layout.pdf", Metadata = new DocumentMetadata { PageCount = 1 },
                Images = [new ImageManifestEntry
                {
                    Hash = "layout", RelativePath = "images/layout.png", MediaType = "image/png",
                    Width = 100, Height = 100, PageNumbers = [1]
                }]
            };
            await recognizer.RecognizeAsync(workspace, index, CancellationToken.None);
            Assert.Equal(fallback ? 1 : 2, handler.Prompts.Count);
            if (failSecondRegion)
            {
                Assert.False(File.Exists(workspace.RecognitionPath("layout")));
                Assert.True(File.Exists(workspace.RecognitionErrorPath("layout")));
                Assert.False(File.Exists(workspace.RecognitionPath("layout") + ".tmp"));
                return;
            }

            if (!fallback)
            {
                Assert.Contains("text region", handler.Prompts[0]);
                Assert.Contains("genuine table", handler.Prompts[1]);
                Assert.All(handler.Sizes, size => Assert.Equal((38, 78), size));
                Assert.Equal([SKColors.Black, SKColors.Blue], handler.Colors);
            }
            else
            {
                Assert.Equal((100, 100), Assert.Single(handler.Sizes));
            }

            using var expectedStream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(expectedStream))
            {
                new MarkdownJsonParser().Write(writer, fallback ? "Part 1" : "Part 1\n\nPart 2");
            }

            using var expected = JsonDocument.Parse(expectedStream.ToArray());
            using var actual = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("layout")));
            Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
            await recognizer.RecognizeAsync(workspace, index, CancellationToken.None);
            Assert.Equal(1, detector.Calls); // Existing per-image recognition cache is unchanged.
        }
        finally
        {
            await workspace.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static FlexyParseOptions Settings() => new()
    {
        ImageLayout = new() { Enabled = true, ModelPath = "missing-layout-model.onnx" },
        ModelApi = new() { MaxAttempts = 1, BaseUrl = "http://localhost/v1/", RequireApiKey = false }
    };

    private static ImageLayoutProcessor Processor(IImageLayoutDetector detector, FlexyParseOptions settings) =>
        new(detector, Microsoft.Extensions.Options.Options.Create(settings), NullLogger<ImageLayoutProcessor>.Instance);

    private static SKBitmap TestImage()
    {
        var bitmap = new SKBitmap(100, 100);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(10, 10, 30, 70, paint);
        paint.Color = SKColors.Blue;
        canvas.DrawRect(60, 10, 30, 70, paint);
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class FakeDetector(Func<SKBitmap, CancellationToken, IReadOnlyList<ImageLayoutRegion>> detect) : IImageLayoutDetector
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        internal int Calls { get; private set; }
        public ValueTask<IReadOnlyList<ImageLayoutRegion>> DetectAsync(SKBitmap image, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(detect(image, cancellationToken));
        }
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CropHandler(bool failSecond) : HttpMessageHandler
    {
        internal List<string> Prompts { get; } = [];
        internal List<(int, int)> Sizes { get; } = [];
        internal List<SKColor> Colors { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var content = document.RootElement.GetProperty("messages")[1].GetProperty("content");
            Prompts.Add(content[0].GetProperty("text").GetString()!);
            var url = content[1].GetProperty("image_url").GetProperty("url").GetString()!;
            Assert.StartsWith("data:image/png;base64,", url);
            using var bitmap = SKBitmap.Decode(Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]));
            Sizes.Add((bitmap.Width, bitmap.Height));
            Colors.Add(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
            return new HttpResponseMessage(failSecond && Prompts.Count == 2 ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content = "Part " + Prompts.Count } } }
                }), Encoding.UTF8, "application/json")
            };
        }
    }
}
