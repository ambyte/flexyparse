using FlexyParse.Hosting;
using FlexyParse.Nemotron;
using FlexyParse.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlexyParse.Tests;

public sealed class NemotronGenerationGuardTests
{
    [Theory]
    [InlineData(1, 16384)]
    [InlineData(4096, 12289)]
    [InlineData(8192, 8193)]
    [InlineData(16383, 2)]
    [InlineData(16384, 1)]
    public void GenerationUsesAllValidDecoderPositionsIncludingFinalEosPrediction(int prefixLength, int expected)
    {
        var steps = NemotronGenerationGuard.GetGenerationSteps(prefixLength);
        Assert.Equal(expected, steps);
        Assert.Equal(NemotronGenerationGuard.ContextLength, prefixLength + steps - 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16385)]
    public void InvalidPrefixFailsBeforeDecoderRuns(int prefixLength)
    {
        Assert.Throws<InvalidDataException>(() => NemotronGenerationGuard.GetGenerationSteps(prefixLength));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(256)]
    public void DetectsSustainedExactLoopsAtDifferentPeriods(int period)
    {
        var options = new NemotronOptions();
        var required = Math.Max(options.RepetitionDetectionMinTokens, period * options.RepetitionDetectionMinRepeats);
        var tokens = Enumerable.Range(0, required - 1).Select(index => index % period).ToList();
        NemotronGenerationGuard.ThrowIfRepetitive(tokens, options);
        tokens.Add((required - 1) % period);

        var error = Assert.Throws<NemotronRepetitionException>(() => NemotronGenerationGuard.ThrowIfRepetitive(tokens, options));
        Assert.Contains($"period {period},", error.Message);
        Assert.Contains("incomplete OCR was discarded", error.Message);
        Assert.Equal(tokens.Count, error.GeneratedTokenCount);
        Assert.Equal(period, error.RepetitionPeriod);
        Assert.Equal(tokens.TakeLast(period), error.RepeatedTokenIds);
        Assert.Contains($"generated {tokens.Count} tokens", error.Message);
    }

    [Fact]
    public void DetectsLoopAfterNonrepetitiveContent()
    {
        var tokens = Enumerable.Range(1000, 600).ToList();
        tokens.AddRange(Enumerable.Range(0, 512).Select(index => index % 16));
        Assert.Throws<NemotronRepetitionException>(() => NemotronGenerationGuard.ThrowIfRepetitive(tokens, new NemotronOptions()));
    }

    [Fact]
    public void LongNonrepetitiveOutputIsNotLimitedToOldTokenBudgets()
    {
        var options = new NemotronOptions();
        var tokens = new List<int>();
        for (var index = 0; index < 12000; index++)
        {
            tokens.Add(index);
            NemotronGenerationGuard.ThrowIfRepetitive(tokens, options);
        }
    }

    [Fact]
    public void SimilarTableRowsWithChangingValuesAreNotLoops()
    {
        var options = new NemotronOptions();
        var tokens = new List<int>();
        for (var row = 0; row < 200; row++)
        {
            foreach (var token in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 1000 + row, 9, 10, 11, 12, 13, 14, 15 })
            {
                tokens.Add(token);
                NemotronGenerationGuard.ThrowIfRepetitive(tokens, options);
            }
        }
    }

    [Fact]
    public void IsolatedRepeatedBlocksAreNotLoops()
    {
        var tokens = Enumerable.Range(0, 1600).Select(index => index % 200).ToList();
        tokens.Insert(800, -1);
        NemotronGenerationGuard.ThrowIfRepetitive(tokens, new NemotronOptions());
    }

    [Fact]
    public void OneDifferentTokenBreaksThePeriodicTail()
    {
        var tokens = Enumerable.Range(0, 512).Select(index => index % 64).ToArray();
        tokens[256] = -1;
        NemotronGenerationGuard.ThrowIfRepetitive(tokens, new NemotronOptions());
    }

    [Fact]
    public void DetectionThresholdsAreConfigurable()
    {
        var options = new NemotronOptions
        {
            RepetitionDetectionMinTokens = 1024,
            RepetitionDetectionMinRepeats = 16,
            RepetitionDetectionMaxPeriod = 64
        };
        var tokens = Enumerable.Range(0, 1023).Select(index => index % 64).ToList();
        NemotronGenerationGuard.ThrowIfRepetitive(tokens, options);
        tokens.Add(63);
        Assert.Throws<NemotronRepetitionException>(() => NemotronGenerationGuard.ThrowIfRepetitive(tokens, options));

        var longerPeriod = Enumerable.Range(0, 4096).Select(index => index % 128).ToArray();
        NemotronGenerationGuard.ThrowIfRepetitive(longerPeriod, options);
    }

    [Theory]
    [InlineData(128, 4, 1)]
    [InlineData(8192, 16, 512)]
    public void DetectionOptionBoundariesAreValid(int minTokens, int minRepeats, int maxPeriod)
    {
        Assert.True(new NemotronOptions
        {
            RepetitionDetectionMinTokens = minTokens,
            RepetitionDetectionMinRepeats = minRepeats,
            RepetitionDetectionMaxPeriod = maxPeriod
        }.IsValid());
    }

    [Theory]
    [InlineData("NemotronCPU")]
    [InlineData("NemotronGPU")]
    public async Task HostBindsDetectionSettingsAndIgnoresLegacyBudget(string providerName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:Recognition:Provider"] = providerName,
            [NemotronOptions.SectionName + ":MaxNewTokens"] = "1",
            [NemotronOptions.SectionName + ":RepetitionDetectionMinTokens"] = "1024",
            [NemotronOptions.SectionName + ":RepetitionDetectionMinRepeats"] = "12",
            [NemotronOptions.SectionName + ":RepetitionDetectionMaxPeriod"] = "128",
            [NemotronOptions.SectionName + ":RetryRepetitiveImages"] = "false",
            [NemotronOptions.SectionName + ":UpscaleRepetitiveImages"] = "false",
            [NemotronOptions.SectionName + ":RepetitionRecoveryScale"] = "2",
            [NemotronOptions.SectionName + ":RepetitionRecoveryPaddingPixels"] = "8",
            [NemotronOptions.SectionName + ":RepetitionRecoveryMaxPixels"] = "500000",
            [NemotronOptions.SectionName + ":RepetitionRecoveryMaxRegions"] = "4",
            [NemotronOptions.SectionName + ":RepetitionRecoveryTimeoutSeconds"] = "30",
            ["FlexyParse:ImageLayout:WhitespaceFallback"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredFlexyParse(configuration);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NemotronOptions>>().Value;
        Assert.Equal(1024, options.RepetitionDetectionMinTokens);
        Assert.Equal(12, options.RepetitionDetectionMinRepeats);
        Assert.Equal(128, options.RepetitionDetectionMaxPeriod);
        Assert.False(options.RetryRepetitiveImages);
        Assert.False(options.UpscaleRepetitiveImages);
        Assert.Equal(2, options.RepetitionRecoveryScale);
        Assert.Equal(8, options.RepetitionRecoveryPaddingPixels);
        Assert.Equal(500000, options.RepetitionRecoveryMaxPixels);
        Assert.Equal(4, options.RepetitionRecoveryMaxRegions);
        Assert.Equal(30, options.RepetitionRecoveryTimeoutSeconds);
        Assert.False(provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value.ImageLayout.WhitespaceFallback);
    }
}
