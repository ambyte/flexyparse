using FlexyParse.Nemotron;
using SkiaSharp;

namespace FlexyParse.Tests;

public sealed class NemotronRecoveryImageTests
{
    [Fact]
    public void DefaultRenderingMatchesTheAcceptedThreeTimesScaleAndWhiteBorder()
    {
        var options = new NemotronOptions();
        Assert.True(NemotronRecoveryImage.TryGetSize(127, 145, options, out var size));
        Assert.Equal(new SKSizeI(405, 459), size);
        using var original = new SKBitmap(127, 145);
        original.Erase(SKColors.Black);
        using var result = NemotronRecoveryImage.Render(original, new(0, 0, 127, 145), size, 12, CancellationToken.None);
        Assert.Equal(SKColors.White, result.GetPixel(0, 0));
        Assert.Equal(SKColors.White, result.GetPixel(404, 458));
        Assert.Equal(SKColors.Black, result.GetPixel(50, 50));
        Assert.Equal(SKColors.Black, original.GetPixel(0, 0));
        Assert.Equal(127, original.Width);
    }

    [Theory]
    [InlineData(573, 408)]
    [InlineData(800, 600)]
    [InlineData(1600, 200)]
    public void RenderingDimensionsRespectPixelAndCanvasLimits(int width, int height)
    {
        var options = new NemotronOptions();
        Assert.True(NemotronRecoveryImage.TryGetSize(width, height, options, out var size));
        Assert.InRange(size.Width, width + 24, options.MaxWidth);
        Assert.InRange(size.Height, height + 24, options.MaxHeight);
        Assert.True((long)size.Width * size.Height <= options.RepetitionRecoveryMaxPixels);
        Assert.InRange(Math.Abs((size.Width - 24d) / width - (size.Height - 24d) / height), 0, 0.01);
    }

    [Fact]
    public void StricterInputLimitsAlsoConstrainPreparedImages()
    {
        var options = new NemotronOptions { MaxImageDimension = 256, MaxImagePixels = 4096 };
        Assert.True(NemotronRecoveryImage.TryGetSize(24, 24, options, out var size));
        Assert.True((long)size.Width * size.Height <= 4096);
        Assert.InRange(size.Width, 48, 64);
    }

    [Theory]
    [InlineData(1000, 1100)]
    [InlineData(1700, 100)]
    [InlineData(0, 100)]
    public void DoesNotDownsizeOrAllocateAnImageThatCannotFit(int width, int height) =>
        Assert.False(NemotronRecoveryImage.TryGetSize(width, height, new(), out _));

    [Fact]
    public void RenderingObservesCancellationBeforeAllocating()
    {
        using var source = new SKBitmap(100, 100);
        Assert.ThrowsAny<OperationCanceledException>(() => NemotronRecoveryImage.Render(source, new(0, 0, 100, 100),
            new(324, 324), 12, new CancellationToken(true)));
    }

    [Theory]
    [InlineData("RepetitionRecoveryScale", 0)]
    [InlineData("RepetitionRecoveryScale", 5)]
    [InlineData("RepetitionRecoveryPaddingPixels", -1)]
    [InlineData("RepetitionRecoveryPaddingPixels", 33)]
    [InlineData("RepetitionRecoveryMaxPixels", 4095)]
    [InlineData("RepetitionRecoveryMaxPixels", 4000001)]
    [InlineData("RepetitionRecoveryMaxRegions", 0)]
    [InlineData("RepetitionRecoveryMaxRegions", 17)]
    [InlineData("RepetitionRecoveryTimeoutSeconds", 0)]
    [InlineData("RepetitionRecoveryTimeoutSeconds", 601)]
    public void ValidatesRecoverySettings(string property, int value)
    {
        var options = new NemotronOptions();
        typeof(NemotronOptions).GetProperty(property)!.SetValue(options, value);
        Assert.False(options.IsValid());
    }
}
