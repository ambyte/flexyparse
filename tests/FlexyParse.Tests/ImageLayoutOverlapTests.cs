using FlexyParse.ImageLayout;
using FlexyParse.Options;
using FlexyParse.Model;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FlexyParse.Tests;

public sealed class ImageLayoutOverlapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitsMinorOverlapAlongEitherAxis(bool vertical)
    {
        var first = Region("Text", 10, 10, 101, 90);
        var second = Region("Text", 99, 10, 190, 90);
        if (vertical)
        {
            first = Transpose(first);
            second = Transpose(second);
        }

        var plan = ImageLayoutPlanner.Plan([second, first], 200, 200, new(), out var reason,
            out var adjusted, out var nested);

        Assert.Null(reason);
        Assert.Equal(1, adjusted);
        Assert.Equal(0, nested);
        Assert.Equal(2, plan!.Count);
        Assert.Equal(vertical ? new SKRectI(10, 10, 90, 100) : new SKRectI(10, 10, 100, 90), plan[0].Bounds);
        Assert.Equal(vertical ? new SKRectI(10, 100, 90, 190) : new SKRectI(100, 10, 190, 90), plan[1].Bounds);
        AssertNoOverlaps(plan);
    }

    [Fact]
    public void CorrectionIsDeterministicAndDoesNotMutateDetections()
    {
        var first = Region("Text", 10, 10, 101, 90);
        var second = Region("Table", 99, 10, 190, 90);
        var third = Region("Text", 10, 100, 190, 190);
        var expected = ImageLayoutPlanner.Plan([first, second, third], 200, 200, new());
        foreach (var input in new[]
        {
            new[] { first, third, second }, new[] { second, first, third }, new[] { second, third, first },
            new[] { third, first, second }, new[] { third, second, first }
        })
        {
            Assert.Equal(expected!, ImageLayoutPlanner.Plan(input, 200, 200, new())!);
        }

        Assert.Equal(new SKRectI(10, 10, 101, 90), first.Bounds);
        Assert.Equal(new SKRectI(99, 10, 190, 90), second.Bounds);
    }

    [Fact]
    public void StrictModeRejectsMinorOverlap()
    {
        var detections = new[] { Region("Text", 10, 10, 101, 90), Region("Text", 99, 10, 190, 90) };
        Assert.Null(ImageLayoutPlanner.Plan(detections, 200, 200, new() { MaxOverlapPixels = 0 }));
        Assert.Null(ImageLayoutPlanner.Plan(detections, 200, 200, new() { MaxOverlapRatio = 0 }));
    }

    [Theory]
    [InlineData(90, 200)] // Ten pixels is too deep even for large blocks.
    [InlineData(96, 110)] // Four pixels is too much of the narrow second block.
    public void RejectsOverlapExceedingEitherBudget(int secondLeft, int secondRight)
    {
        var plan = ImageLayoutPlanner.Plan(
            [Region("Text", 0, 0, 100, 100), Region("Text", secondLeft, 0, secondRight, 100)],
            300, 200, new(), out var reason);
        Assert.Null(plan);
        Assert.Equal("Distinct regions overlap", reason);
    }

    [Fact]
    public void EnforcesCumulativeAreaBudgetAcrossSeveralCuts()
    {
        // Each two-pixel intrusion passes the 3% pair limit, but four one-pixel cuts lose 3.96% of the center.
        var constrained = new ImageLayoutOptions { MaxOverlapPixels = 4, MaxOverlapRatio = 0.03 };
        List<ImageLayoutRegion> regions = [
            Region("Text", 98, 98, 198, 198), Region("Text", 0, 98, 100, 198), Region("Text", 196, 98, 296, 198),
            Region("Text", 98, 0, 198, 100), Region("Text", 98, 196, 198, 296)
        ];
        Assert.False(ImageLayoutOverlapResolver.TryResolve(regions, constrained, out var adjusted, out _));
        Assert.Equal(3, adjusted);
    }

    [Theory]
    [InlineData("Table", "Text")]
    [InlineData("Table", "Formula")]
    [InlineData("Table", "Table")]
    [InlineData("Picture", "Caption")]
    [InlineData("Picture", "Formula")]
    [InlineData("Picture", "Picture")]
    [InlineData("Formula", "Text")]
    [InlineData("Text", "Title")]
    public void AbsorbsCompatibleNestedRegionWithoutChangingContainer(string outerLabel, string innerLabel)
    {
        var outer = Region(outerLabel, 0, 0, 100, 100) with { Confidence = 0.5 };
        var inner = Region(innerLabel, 20, 20, 80, 80);
        var separate = Region("Text", 120, 0, 200, 100);
        var plan = ImageLayoutPlanner.Plan([inner, separate, outer], 220, 120, new(), out var reason,
            out var adjusted, out var nested);
        Assert.Null(reason);
        Assert.Equal(0, adjusted);
        Assert.Equal(1, nested);
        Assert.Equal([outer, separate], plan!);
        AssertNoOverlaps(plan!);
    }

    [Theory]
    [InlineData("Text", "Table")]
    [InlineData("Text", "Picture")]
    [InlineData("Picture", "Table")]
    [InlineData("Formula", "Table")]
    public void PreservesFallbackForIncompatibleContainment(string outerLabel, string innerLabel)
    {
        Assert.Null(ImageLayoutPlanner.Plan([
            Region(outerLabel, 0, 0, 100, 100), Region(innerLabel, 20, 20, 80, 80)
        ], 200, 200, new()));
    }

    [Fact]
    public void NestedResolutionCanBeDisabled()
    {
        Assert.Null(ImageLayoutPlanner.Plan([
            Region("Table", 0, 0, 100, 100), Region("Text", 20, 20, 80, 80)
        ], 200, 200, new() { ResolveNestedRegions = false }));
    }

    [Fact]
    public void AbsorbsNestedChainsAndRetainsOtherRegions()
    {
        var outer = Region("Picture", 0, 0, 100, 100);
        var inner = Region("Picture", 10, 10, 90, 90);
        var text = Region("Text", 20, 20, 80, 80);
        var separate = Region("Text", 120, 0, 200, 100);
        Assert.Equal([outer, separate], ImageLayoutPlanner.Plan([text, inner, outer, separate], 220, 120, new())!);
    }

    [Fact]
    public void NearContainmentWithProtrudingContentIsNotDiscarded()
    {
        Assert.Null(ImageLayoutPlanner.Plan([
            Region("Table", 10, 10, 110, 110), Region("Text", 20, 20, 111, 80)
        ], 200, 200, new()));
    }

    [Fact]
    public void FindsDuplicatesBeforeResolvingOtherOverlaps()
    {
        var first = Region("Text", 0, 0, 100, 100);
        var duplicate = Region("Title", 0, 0, 99, 100) with { Confidence = 0.8 };
        var neighbor = Region("Text", 98, 0, 198, 100);
        var plan = ImageLayoutPlanner.Plan([neighbor, duplicate, first], 200, 120, new(), out _, out var adjusted, out var nested);
        Assert.Equal(2, plan!.Count);
        Assert.Equal(1, adjusted);
        Assert.Equal(0, nested);
        AssertNoOverlaps(plan);
    }

    [Fact]
    public void OrdersTouchingColumnsAndBandsWithoutDroppingAPixel()
    {
        var title = Region("Title", 0, 0, 200, 20);
        var left = Region("Text", 0, 20, 100, 100);
        var right = Region("Text", 100, 20, 200, 100);
        Assert.Equal([title, left, right], ImageLayoutPlanner.Plan([right, title, left], 200, 100, new())!);
    }

    [Fact]
    public async Task ProcessorAcceptsCorrectedCropsAndReportsResolution()
    {
        using var bitmap = new SKBitmap(200, 100);
        bitmap.Erase(SKColors.White);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black })
            canvas.DrawRect(10, 10, 180, 80, paint);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var settings = new FlexyParseOptions { ImageLayout = new() { Enabled = true } };
        var logger = new RecordingLogger<ImageLayoutProcessor>();
        var processor = new ImageLayoutProcessor(new Detector([
            Region("Text", 10, 10, 101, 90), Region("Text", 99, 10, 190, 90),
            Region("Caption", 20, 20, 80, 80)
        ]), Microsoft.Extensions.Options.Options.Create(settings), logger);
        using var plan = await processor.PrepareAsync(encoded.ToArray(), CancellationToken.None);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Regions.Count);
        AssertNoOverlaps(plan.Regions);
        var resolution = Assert.Single(logger.Entries, entry => entry.Properties.ContainsKey("AdjustedOverlaps"));
        Assert.Equal(1, resolution.Properties["AdjustedOverlaps"]);
        Assert.Equal(1, resolution.Properties["RemovedNestedRegions"]);
        Assert.Equal(1, ImageLayoutPlanner.MeasureContentCoverage(bitmap, plan.Regions, settings.ImageLayout, CancellationToken.None));
    }

    [Fact]
    public async Task CoverageGuardRejectsContentLostDuringLocalCorrection()
    {
        using var bitmap = new SKBitmap(200, 100);
        bitmap.Erase(SKColors.White);
        // This content sits in the part of the first box lost by a horizontal trim, outside the second box.
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black })
            canvas.DrawRect(100, 10, 1, 10, paint);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var settings = new FlexyParseOptions { ImageLayout = new() { Enabled = true, PaddingPixels = 0 } };
        var processor = new ImageLayoutProcessor(new Detector([
            Region("Text", 10, 10, 101, 90), Region("Text", 99, 30, 190, 90)
        ]), Microsoft.Extensions.Options.Options.Create(settings), NullLogger<ImageLayoutProcessor>.Instance);
        using var plan = await processor.PrepareAsync(encoded.ToArray(), CancellationToken.None);
        Assert.Null(plan);
    }

    [Fact]
    public async Task PipelineRecognizesResolvedRegionsOnceInReadingOrder()
    {
        using var bitmap = new SKBitmap(200, 100);
        bitmap.Erase(SKColors.White);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black })
        {
            canvas.DrawRect(10, 10, 90, 80, paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(100, 10, 90, 80, paint);
        }

        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var settings = new FlexyParseOptions { ImageLayout = new() { Enabled = true } };
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var processor = new ImageLayoutProcessor(new Detector([
            Region("Table", 99, 10, 190, 90), Region("Text", 10, 10, 101, 90),
            Region("Text", 110, 20, 180, 80)
        ]), options, NullLogger<ImageLayoutProcessor>.Instance);
        var pipeline = new ImageRecognitionPipeline(options, processor);
        await using var workspace = JobWorkspace.Create(settings.ResolveTempDirectory(), "resolved.png");
        var image = new ImageManifestEntry { Hash = "resolved", RelativePath = "images/resolved.png", MediaType = "image/png" };
        var kinds = new List<string?>();
        var colors = new List<SKColor>();
        var output = await pipeline.RecognizeAsync(encoded.ToArray(), workspace, image, (bytes, mediaType, kind, _) =>
        {
            using var crop = SKBitmap.Decode(bytes);
            Assert.Equal("image/png", mediaType);
            Assert.Equal(90, crop.Width);
            Assert.Equal(80, crop.Height);
            kinds.Add(kind);
            colors.Add(crop.GetPixel(crop.Width / 2, crop.Height / 2));
            return Task.FromResult(kind!);
        }, CancellationToken.None);

        Assert.Equal("text\n\ntable", output);
        Assert.Equal(["text", "table"], kinds);
        Assert.Equal([SKColors.Black, SKColors.Blue], colors);
    }

    private static ImageLayoutRegion Region(string label, int left, int top, int right, int bottom) =>
        new(label, new SKRectI(left, top, right, bottom), 0.9);

    private static ImageLayoutRegion Transpose(ImageLayoutRegion region) => region with
    {
        Bounds = new SKRectI(region.Bounds.Top, region.Bounds.Left, region.Bounds.Bottom, region.Bounds.Right)
    };

    private static void AssertNoOverlaps(IReadOnlyList<ImageLayoutRegion> regions)
    {
        for (var i = 0; i < regions.Count; i++)
            for (var j = i + 1; j < regions.Count; j++)
                Assert.False(SKRectI.Intersect(regions[i].Bounds, regions[j].Bounds).Width > 0
                    && SKRectI.Intersect(regions[i].Bounds, regions[j].Bounds).Height > 0);
    }

    private sealed class Detector(IReadOnlyList<ImageLayoutRegion> regions) : IImageLayoutDetector
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<ImageLayoutRegion>> DetectAsync(SKBitmap image, CancellationToken cancellationToken) =>
            ValueTask.FromResult(regions);
    }
}
