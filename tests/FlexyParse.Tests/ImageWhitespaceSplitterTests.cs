using FlexyParse.ImageLayout;
using FlexyParse.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FlexyParse.Tests;

public sealed class ImageWhitespaceSplitterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitsLightAndDarkColumnsWithoutLosingOrDuplicatingPixels(bool dark)
    {
        using var bitmap = Columns(dark);
        var tiles = ImageWhitespaceSplitter.Split(bitmap, 2, CancellationToken.None);
        Assert.Equal(2, tiles.Count);
        Assert.Equal(new SKRectI(0, 0, 200, 240), tiles[0].Bounds);
        Assert.Equal(new SKRectI(200, 0, 400, 240), tiles[1].Bounds);
        Assert.All(tiles, tile => Assert.Null(ImageLayoutPlanner.RegionKind(tile.Label)));
        AssertFullCoverage(bitmap, tiles);
    }

    [Fact]
    public void HorizontalBandsAreUsedWhenThereIsNoColumnGutter()
    {
        using var rotated = new SKBitmap(240, 400);
        rotated.Erase(SKColors.White);
        using (var canvas = new SKCanvas(rotated))
        using (var paint = new SKPaint { Color = SKColors.Black })
        {
            canvas.DrawRect(20, 20, 200, 140, paint);
            canvas.DrawRect(20, 240, 200, 140, paint);
        }
        var tiles = ImageWhitespaceSplitter.Split(rotated, 2, CancellationToken.None);
        Assert.Equal(2, tiles.Count);
        Assert.Equal(200, tiles[0].Bounds.Bottom);
        Assert.Equal(200, tiles[1].Bounds.Top);
        AssertFullCoverage(rotated, tiles);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(16)]
    [InlineData(256)]
    public void RecursiveSplitsRespectTheBudgetAndCoverTheWholeImage(int budget)
    {
        using var bitmap = Columns();
        var tiles = ImageWhitespaceSplitter.Split(bitmap, budget, CancellationToken.None);
        Assert.InRange(tiles.Count, 1, Math.Min(budget, 16));
        AssertFullCoverage(bitmap, tiles);
    }

    [Fact]
    public void BlankImagesAndMarginsDoNotProduceEmptyTiles()
    {
        using var bitmap = new SKBitmap(400, 240);
        bitmap.Erase(SKColors.White);
        Assert.Single(ImageWhitespaceSplitter.Split(bitmap, 16, CancellationToken.None));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(100, 40, 200, 160, paint);
        Assert.Single(ImageWhitespaceSplitter.Split(bitmap, 16, CancellationToken.None));
    }

    [Fact]
    public void ASpanningLinePreventsAColumnCut()
    {
        using var bitmap = Columns();
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(20, 20, 360, 12, paint);
        var tiles = ImageWhitespaceSplitter.Split(bitmap, 2, CancellationToken.None);
        Assert.All(tiles, tile => Assert.Equal(400, tile.Bounds.Width));
    }

    [Fact]
    public void NarrowGuttersAreNotUsed()
    {
        using var bitmap = new SKBitmap(400, 240);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(20, 20, 178, 200, paint);
        canvas.DrawRect(202, 20, 178, 200, paint);
        Assert.Single(ImageWhitespaceSplitter.Split(bitmap, 2, CancellationToken.None));
    }

    [Fact]
    public void FindsNarrowPageGuttersOnEitherSideOfAColoredSeparator()
    {
        using var bitmap = new SKBitmap(573, 408);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(23, 20, 300, 368, paint);
        canvas.DrawRect(344, 20, 217, 368, paint);
        paint.Color = SKColors.Blue;
        canvas.DrawRect(332, 16, 2, 376, paint);
        var tiles = ImageWhitespaceSplitter.Split(bitmap, 2, CancellationToken.None);
        Assert.Equal(2, tiles.Count);
        Assert.InRange(tiles[0].Bounds.Right, 334, 343);
        AssertFullCoverage(bitmap, tiles);
    }

    [Fact]
    public void CancellationIsObservedBeforeScanning()
    {
        using var bitmap = Columns();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageWhitespaceSplitter.Split(bitmap, 2, new CancellationToken(true)));
    }

    [Fact]
    public void RecoveryTuningCanSplitSmallerBlocksWithoutChangingLayoutDefaults()
    {
        using var bitmap = new SKBitmap(100, 100);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(10, 10, 30, 80, paint);
        canvas.DrawRect(60, 10, 30, 80, paint);
        Assert.Single(ImageWhitespaceSplitter.Split(bitmap, 2, CancellationToken.None));
        var tiles = ImageWhitespaceSplitter.Split(bitmap, 2, CancellationToken.None, minimumSide: 32, minimumGapPixels: 2);
        Assert.Equal(2, tiles.Count);
        AssertFullCoverage(bitmap, tiles);
    }

    [Fact]
    public void RecoveryCanIgnoreSparseMarginInkWithoutLosingSourcePixels()
    {
        using var bitmap = new SKBitmap(200, 200);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(20, 20, 160, 65, paint);
        canvas.DrawRect(20, 115, 160, 65, paint);
        canvas.DrawRect(2, 10, 2, 180, paint);
        Assert.Single(ImageWhitespaceSplitter.Split(bitmap, 2, CancellationToken.None));
        var tiles = ImageWhitespaceSplitter.Split(bitmap, 2, CancellationToken.None, 32, 2, 0.02);
        Assert.Equal(2, tiles.Count);
        Assert.Equal(100, tiles[0].Bounds.Bottom);
        AssertFullCoverage(bitmap, tiles);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ProcessorUsesFullCoverageTilesAfterOverlapOrCoverageRejection(bool overlap, bool enabled)
    {
        using var bitmap = Columns(true);
        var settings = new FlexyParseOptions();
        settings.ImageLayout.Enabled = true;
        settings.ImageLayout.MaxRegions = 2;
        settings.ImageLayout.WhitespaceFallback = enabled;
        var processor = new ImageLayoutProcessor(new RejectedDetector(overlap), Microsoft.Extensions.Options.Options.Create(settings),
            NullLogger<ImageLayoutProcessor>.Instance);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var plan = await processor.PrepareAsync(encoded.ToArray(), CancellationToken.None);
        if (!enabled)
        {
            Assert.Null(plan);
            return;
        }
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Regions.Count);
        AssertFullCoverage(bitmap, plan.Regions);
        foreach (var tile in plan.Regions)
        {
            using var crop = SKBitmap.Decode(plan.Crop(tile));
            Assert.Equal(tile.Bounds.Width, crop.Width);
            Assert.Equal(tile.Bounds.Height, crop.Height);
        }
    }

    internal static SKBitmap Columns(bool dark = false)
    {
        var bitmap = new SKBitmap(400, 240);
        bitmap.Erase(dark ? new SKColor(32, 32, 32) : SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = dark ? SKColors.White : SKColors.Black };
        for (var y = 20; y < 220; y += 20)
        {
            canvas.DrawRect(20, y, 140, 12, paint);
            canvas.DrawRect(240, y, 140, 12, paint);
        }
        return bitmap;
    }

    private static void AssertFullCoverage(SKBitmap bitmap, IReadOnlyList<ImageLayoutRegion> tiles)
    {
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            Assert.Single(tiles, tile => tile.Bounds.Contains(x, y));
    }

    private sealed class RejectedDetector(bool overlap) : IImageLayoutDetector
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<ImageLayoutRegion>> DetectAsync(SKBitmap image, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ImageLayoutRegion>>(overlap
                ? [new("Text", new(20, 20, 260, 210), 0.9), new("Text", new(150, 30, 380, 220), 0.9)]
                : [new("Text", new(20, 20, 160, 212), 0.9), new("Text", new(240, 20, 380, 212), 0.9)]);
    }
}
