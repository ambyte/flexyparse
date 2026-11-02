using FlexyParse.Nemotron;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FlexyParse.Tests;

public sealed class NemotronRepetitionRecoveryTests
{
    [Fact]
    public async Task DefaultRecoveryUpscalesAnUnsplitImageAndPreservesTheOriginal()
    {
        await WithImage(async path =>
        {
            using (var image = new SKBitmap(127, 145))
            {
                image.Erase(SKColors.Black);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(path, encoded.ToArray());
            }
            var original = await File.ReadAllBytesAsync(path);
            var calls = 0;
            string? preparedPath = null;
            var result = await Recover(path, new(), (source, _) =>
            {
                calls++;
                if (source == path) throw new NemotronRepetitionException("loop");
                preparedPath = source;
                using var bitmap = SKBitmap.Decode(source);
                Assert.Equal((405, 459), (bitmap.Width, bitmap.Height));
                Assert.Equal(SKColors.White, bitmap.GetPixel(0, 0));
                Assert.Equal(SKColors.Black, bitmap.GetPixel(50, 50));
                return Task.FromResult("accepted approximate transcription");
            });
            Assert.Equal("accepted approximate transcription", result);
            Assert.Equal(2, calls);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(preparedPath));
        });
    }

    [Fact]
    public async Task RecoveryHonorsRegionBudgetAndKeepsRepeatedSourceText()
    {
        await WithImage(async path =>
        {
            var paths = new List<string>();
            var result = await Recover(path, new() { RepetitionRecoveryMaxRegions = 2 }, (source, _) =>
            {
                paths.Add(source);
                if (source == path) throw new NemotronRepetitionException("loop");
                using var bitmap = SKBitmap.Decode(source);
                Assert.Equal((624, 744), (bitmap.Width, bitmap.Height));
                return Task.FromResult("identical text");
            });
            Assert.Equal("identical text\n\nidentical text", result);
            Assert.Equal(3, paths.Count);
            Assert.All(paths.Skip(1), source => Assert.False(File.Exists(source)));
        });
    }

    [Fact]
    public async Task RepeatedPreprocessedFailureDoesNotRetryRecursively()
    {
        await WithImage(async path =>
        {
            var calls = 0;
            await Assert.ThrowsAsync<NemotronRepetitionException>(() => Recover(path,
                new() { RepetitionRecoveryMaxRegions = 1 }, (_, _) =>
                {
                    calls++;
                    throw new NemotronRepetitionException("loop");
                }));
            Assert.Equal(2, calls);
        });
    }

    [Fact]
    public async Task FailureOfLaterPreprocessedRegionDiscardsEarlierOutput()
    {
        await WithImage(async path =>
        {
            var calls = 0;
            var files = new List<string>();
            await Assert.ThrowsAsync<NemotronRepetitionException>(() => Recover(path,
                new() { RepetitionRecoveryMaxRegions = 2 }, (source, _) =>
                {
                    calls++;
                    if (source != path) files.Add(source);
                    if (calls != 2) throw new NemotronRepetitionException("loop");
                    return Task.FromResult("must be discarded");
                }));
            Assert.Equal(3, calls);
            Assert.All(files, file => Assert.False(File.Exists(file)));
        });
    }

    [Fact]
    public async Task PerRegionTimeoutStopsRecoveryAndCleansTheTemporaryFile()
    {
        await WithImage(async path =>
        {
            string? temporary = null;
            await Assert.ThrowsAsync<TimeoutException>(() => Recover(path,
                new() { RepetitionRecoveryTimeoutSeconds = 1, RepetitionRecoveryMaxRegions = 1 }, async (source, token) =>
                {
                    if (source == path) throw new NemotronRepetitionException("loop");
                    temporary = source;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "unreachable";
                }));
            Assert.NotNull(temporary);
            Assert.False(File.Exists(temporary));
        });
    }

    [Fact]
    public async Task OversizedPreparedRegionFailsBeforeAnyRecoveryCall()
    {
        await WithImage(async path =>
        {
            var calls = 0;
            var failure = new NemotronRepetitionException("loop");
            var actual = await Assert.ThrowsAsync<NemotronRepetitionException>(() => Recover(path,
                new() { RepetitionRecoveryMaxPixels = 4096 }, (_, _) =>
                {
                    calls++;
                    throw failure;
                }));
            Assert.Same(failure, actual);
            Assert.Equal(1, calls);
        });
    }

    [Fact]
    public async Task SuccessfulRecognitionDoesNotReadTheImageOrRetry()
    {
        var calls = 0;
        var result = await Recover("unused", new(), (_, _) =>
        {
            calls++;
            return Task.FromResult("complete output");
        });
        Assert.Equal("complete output", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RecoveryRechecksImageLimitsBeforeDecoding()
    {
        await WithImage(async path =>
        {
            var calls = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => Recover(path, new() { MaxImagePixels = 4096 }, (_, _) =>
            {
                calls++;
                throw new NemotronRepetitionException("loop");
            }));
            Assert.Equal(1, calls);
        });
    }

    [Fact]
    public async Task KeepsIdenticalTextFromDifferentTilesAndDeletesTemporaryFiles()
    {
        await WithImage(async path =>
        {
            var paths = new List<string>();
            var result = await Recover(path, new() { UpscaleRepetitiveImages = false }, (source, _) =>
            {
                paths.Add(source);
                if (source == path) throw new NemotronRepetitionException("suspected loop");
                using var crop = SKBitmap.Decode(source);
                Assert.Equal(200, crop.Width);
                Assert.Equal(240, crop.Height);
                return Task.FromResult("The same genuine text.");
            });
            Assert.Equal("The same genuine text.\n\nThe same genuine text.", result);
            Assert.Equal(3, paths.Count);
            Assert.All(paths.Skip(1), source => Assert.False(File.Exists(source)));
        });
    }

    [Fact]
    public async Task DoesNotRetryOrdinaryErrors()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Recover("unused", new(), (_, _) =>
        {
            calls++;
            throw new InvalidDataException("bad image, not repetition");
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisabledRecoveryDoesNotReadOrRetryTheImage()
    {
        var calls = 0;
        await Assert.ThrowsAsync<NemotronRepetitionException>(() => Recover("unused", new() { RetryRepetitiveImages = false }, (_, _) =>
        {
            calls++;
            throw new NemotronRepetitionException("loop");
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedSecondTileDoesNotReturnPartialTextOrRecurse()
    {
        await WithImage(async path =>
        {
            var calls = 0;
            var crops = new List<string>();
            await Assert.ThrowsAsync<NemotronRepetitionException>(() => Recover(path, new() { UpscaleRepetitiveImages = false }, (source, _) =>
            {
                calls++;
                if (source != path) crops.Add(source);
                if (calls != 2) throw new NemotronRepetitionException("loop");
                return Task.FromResult("must not be returned on its own");
            }));
            Assert.Equal(3, calls);
            Assert.All(crops, crop => Assert.False(File.Exists(crop)));
        });
    }

    [Fact]
    public async Task CancellationDeletesTheCurrentCropAndStopsRecovery()
    {
        await WithImage(async path =>
        {
            using var cancellation = new CancellationTokenSource();
            string? crop = null;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NemotronRepetitionRecovery.RecognizeAsync(path, new(), (source, token) =>
            {
                if (source == path) throw new NemotronRepetitionException("loop");
                crop = source;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult("");
            }, NullLogger.Instance, cancellation.Token));
            Assert.NotNull(crop);
            Assert.False(File.Exists(crop));
        });
    }

    [Fact]
    public async Task NoSafeGutterPreservesTheOriginalFailure()
    {
        await WithImage(async path =>
        {
            using var blank = new SKBitmap(400, 240);
            blank.Erase(SKColors.White);
            using var encoded = blank.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, encoded.ToArray());
            var calls = 0;
            var failure = new NemotronRepetitionException("loop");
            var actual = await Assert.ThrowsAsync<NemotronRepetitionException>(() => Recover(path, new() { UpscaleRepetitiveImages = false }, (_, _) =>
            {
                calls++;
                throw failure;
            }));
            Assert.Same(failure, actual);
            Assert.Equal(1, calls);
        });
    }

    private static Task<string> Recover(string path, NemotronOptions options, Func<string, CancellationToken, Task<string>> recognize) =>
        NemotronRepetitionRecovery.RecognizeAsync(path, options, recognize, NullLogger.Instance, CancellationToken.None);

    private static async Task WithImage(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "source.png");
            using var bitmap = ImageWhitespaceSplitterTests.Columns();
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, encoded.ToArray());
            await action(path);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
