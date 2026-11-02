using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlexyParse.Hosting;
using FlexyParse.Markdown;
using FlexyParse.Model;
using FlexyParse.Nemotron;
using FlexyParse.Options;
using FlexyParse.RapidOcr;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using Xunit.Abstractions;

namespace FlexyParse.Tests;

public sealed class NemotronTests
{
    [Theory]
    [InlineData("NemotronCPU", false)]
    [InlineData("NemotronGPU", false)]
    [InlineData("NemotronCPU", true)]
    [InlineData("NemotronGPU", true)]
    public async Task InferenceThreadsDefaultsToFour(string name, bool useShippedSettings)
    {
        Assert.Equal(4, new NemotronOptions().InferenceThreads);
        var configuration = new ConfigurationBuilder();
        if (useShippedSettings)
            configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:Recognition:Provider"] = name
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredFlexyParse(configuration.Build());
        await using var provider = services.BuildServiceProvider();
        Assert.Equal(4, provider.GetRequiredService<IOptions<NemotronOptions>>().Value.InferenceThreads);
    }

    [Theory]
    [InlineData("NemotronCPU", NemotronExecutionMode.CpuInt8)]
    [InlineData("NemotronGPU", NemotronExecutionMode.WebGpuFp16)]
    public async Task HostInstallsOnlyNemotronWithoutLoadingOrDownloadingModels(string name, NemotronExecutionMode mode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredFlexyParse(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:Recognition:Provider"] = name,
            ["FlexyParse:Recognition:Nemotron:InferenceThreads"] = "2"
        }).Build());
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.IsType<NemotronImageRecognizer>(provider.GetRequiredService<IImageRecognizer>());
        Assert.Single(provider.GetServices<IImageRecognizer>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<NemotronStartupService>());
        Assert.Null(provider.GetService<RapidOcrModelStore>());
        Assert.Equal(2, provider.GetRequiredService<IOptions<NemotronOptions>>().Value.InferenceThreads);
        Assert.Equal(mode, provider.GetRequiredService<IOptions<NemotronOptions>>().Value.ExecutionMode);
        Assert.DoesNotContain(typeof(IImageRecognizer).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "FlexyParse.Recognition.Nemotron");
    }

    [Theory]
    [InlineData("OpenAi")]
    [InlineData("RapidOcr")]
    public async Task OtherProvidersDoNotInstallNemotron(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredFlexyParse(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["FlexyParse:Recognition:Provider"] = name }).Build());
        await using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<NemotronModelStore>());
        Assert.Null(provider.GetService<INemotronSessionFactory>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), service => service is NemotronStartupService);
    }

    [Theory]
    [InlineData("RepetitionDetectionMinTokens", "127")]
    [InlineData("RepetitionDetectionMinTokens", "8193")]
    [InlineData("RepetitionDetectionMinRepeats", "3")]
    [InlineData("RepetitionDetectionMinRepeats", "17")]
    [InlineData("RepetitionDetectionMaxPeriod", "0")]
    [InlineData("RepetitionDetectionMaxPeriod", "513")]
    [InlineData("MaxWidth", "65")]
    [InlineData("MaxHeight", "2049")]
    [InlineData("InferenceThreads", "0")]
    [InlineData("ModelsDirectory", " ")]
    [InlineData("GpuDeviceId", "-1")]
    [InlineData("RepetitionPenalty", "NaN")]
    [InlineData("DownloadTimeoutSeconds", "0")]
    public async Task InvalidOptionsFailOnStartup(string key, string value)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredFlexyParse(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:Recognition:Provider"] = "NemotronCPU",
            [NemotronOptions.SectionName + ":" + key] = value
        }).Build());
        await using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void PinnedPackContainsExternalWeightsAndIntegrityMetadata()
    {
        var files = NemotronModelStore.ReadPack();
        Assert.Equal(19, files.Count);
        Assert.Equal(760646868, files.Sum(file => file.Size));
        foreach (var name in new[] { "patch_embed", "vision_encoder", "embed_tokens", "decoder_model_merged" })
        {
            Assert.Contains(files, file => file.Name == name + ".onnx");
            Assert.Contains(files, file => file.Name == name + ".onnx_data");
        }
        Assert.All(files, file => { Assert.Equal(64, file.Sha256.Length); Assert.True(file.Size > 0); });
    }

    [Fact]
    public void Fp16PackHasPinnedWeightsAndSeparateDefaultCache()
    {
        var files = NemotronModelStore.ReadPack(NemotronExecutionMode.WebGpuFp16);
        Assert.Equal(19, files.Count);
        Assert.Equal(1452242858, files.Sum(file => file.Size));
        Assert.All(files, file => { Assert.Equal(64, file.Sha256.Length); Assert.True(file.Size > 0); });
        foreach (var name in new[] { "patch_embed", "vision_encoder", "embed_tokens", "decoder_model_merged" })
        {
            Assert.Contains(files, file => file.Name == name + ".onnx");
            Assert.Contains(files, file => file.Name == name + ".onnx_data");
        }
        var options = new NemotronOptions();
        Assert.Equal("models/nemotron/275c486f-int8", options.ResolvedModelsDirectory);
        options.ExecutionMode = NemotronExecutionMode.WebGpuFp16;
        Assert.Equal("models/nemotron/275c486f-fp16", options.ResolvedModelsDirectory);
        options.ModelsDirectory = "custom-pack";
        Assert.Equal("custom-pack", options.ResolvedModelsDirectory);
    }

    [Fact]
    public async Task WebGpuConfigurationBindsWithoutInitializingNativeRuntime()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredFlexyParse(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:Recognition:Provider"] = "NemotronGPU",
            [NemotronOptions.SectionName + ":GpuDeviceId"] = "2"
        }).Build());
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<NemotronOptions>>().Value;
        Assert.Equal(NemotronExecutionMode.WebGpuFp16, options.ExecutionMode);
        Assert.Equal(2, options.GpuDeviceId);
        Assert.EndsWith("-fp16", options.ResolvedModelsDirectory);
        Assert.IsType<NemotronImageRecognizer>(provider.GetRequiredService<IImageRecognizer>());
    }

    [Theory]
    [InlineData("NemotronCPU", "WebGpuFp16", NemotronExecutionMode.CpuInt8)]
    [InlineData("NemotronGPU", "CpuInt8", NemotronExecutionMode.WebGpuFp16)]
    [InlineData("NemotronGPU", "99", NemotronExecutionMode.WebGpuFp16)]
    public async Task SelectedProviderOverridesObsoleteExplicitExecutionMode(string name, string configuredMode,
        NemotronExecutionMode expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredFlexyParse(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:Recognition:Provider"] = name,
            [NemotronOptions.SectionName + ":ExecutionMode"] = configuredMode
        }).Build());
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.Equal(expected, provider.GetRequiredService<IOptions<NemotronOptions>>().Value.ExecutionMode);
    }

    [Theory]
    [InlineData(NemotronExecutionMode.CpuInt8, "int8")]
    [InlineData(NemotronExecutionMode.WebGpuFp16, "fp16")]
    public async Task DownloadUsesSelectedVariantAndVerifiedOfflineCache(NemotronExecutionMode mode, string variant)
    {
        using var fixture = new Fixture();
        fixture.Options.ExecutionMode = mode;
        using (var store = fixture.Store()) await store.EnsureAvailableAsync(default);
        Assert.Contains($"/onnx/{variant}/test.bin", fixture.Handler.LastUri!.AbsoluteUri);
        fixture.Options.DownloadModels = false;
        using var offline = fixture.Store();
        Assert.Equal(Path.GetFullPath(fixture.Root), await offline.EnsureAvailableAsync(default));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public void CpuModeDoesNotRequireWebGpuRegistration()
    {
        using var settings = new SessionOptions();
        NemotronSession.ConfigureExecutionProvider(settings, new NemotronOptions());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(7, 2)]
    public void UnavailableWebGpuDeviceFailsExplicitly(int index, int count)
    {
        var error = Assert.Throws<InvalidOperationException>(() => NemotronWebGpuProvider.ValidateDeviceIndex(index, count));
        Assert.Contains($"index {index}", error.Message);
        Assert.Contains($"{count} WebGPU device(s)", error.Message);
        Assert.Contains("No automatic CPU-mode fallback", error.Message);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void WebGpuDeviceSelectionAcceptsAvailableIndices(int index, int count)
    {
        NemotronWebGpuProvider.ValidateDeviceIndex(index, count);
    }

    [Fact]
    public void WebGpuDeviceSelectionRejectsNegativeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NemotronWebGpuProvider.ValidateDeviceIndex(-1, 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void PositionalFieldLoadsBothPinnedNumpyPrecisions(int elementSize)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "pos_embed.npy");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(new byte[] { 0x93, 78, 85, 77, 80, 89, 1, 0 });
            var header = Encoding.ASCII.GetBytes($"{{'descr': '<f{elementSize}', 'fortran_order': False, 'shape': (1, 16384, 1152), }}\n");
            writer.Write((ushort)header.Length);
            writer.Write(header);
            for (var i = 0; i < 16384 * 1152; i++)
            {
                if (elementSize == 2) writer.Write(BitConverter.HalfToUInt16Bits((Half)0.25f));
                else writer.Write(0.25f);
            }
        }
        var positions = new NavitPositions(path);
        var patches = new DenseTensor<float>([4, 1152]);
        patches.Fill(2f);
        var result = positions.Add(patches, new(new DenseTensor<float>([4, 768]), 1, 4, 16, 64), default);
        Assert.All(result.ToArray(), value => Assert.Equal(2.25f, value, 4));
    }

    [Fact]
    public async Task DownloadsOnceAndReusesVerifiedOfflineCache()
    {
        using var fixture = new Fixture();
        using (var store = fixture.Store())
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.EnsureAvailableAsync(default)));
            Assert.Equal(1, fixture.Handler.Calls);
        }
        fixture.Options.DownloadModels = false;
        using var offline = fixture.Store();
        Assert.Equal(Path.GetFullPath(fixture.Root), await offline.EnsureAvailableAsync(default));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task DownloadsLogProgressAndVerificationAndOfflineCacheDoesNotReportDownload()
    {
        using var fixture = new Fixture();
        using (var store = fixture.Store()) await store.EnsureAvailableAsync(default);
        Assert.Contains(fixture.Logger.Entries, entry => Equals(entry.Properties.GetValueOrDefault("DownloadPercent"), 0d));
        Assert.Contains(fixture.Logger.Entries, entry => Equals(entry.Properties.GetValueOrDefault("DownloadPercent"), 100d));
        Assert.Contains(fixture.Logger.Entries, entry => entry.Message.Contains("Verifying SHA-256", StringComparison.Ordinal));
        Assert.Contains(fixture.Logger.Entries, entry => entry.Message.Contains("downloaded and verified", StringComparison.Ordinal));
        fixture.Logger.Entries.Clear();
        fixture.Options.DownloadModels = false;
        using var offline = fixture.Store();
        await offline.EnsureAvailableAsync(default);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Message.Contains("Using verified cached", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Properties.ContainsKey("DownloadPercent"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptDownloadIsNeverPublishedAndTemporaryFilesAreRemoved(bool oversized)
    {
        using var fixture = new Fixture();
        fixture.Handler.Content = oversized ? Encoding.UTF8.GetBytes("too many bytes") : Encoding.UTF8.GetBytes("bad");
        using var store = fixture.Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureAvailableAsync(default));
        Assert.Empty(Directory.GetFiles(fixture.Root));
        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Message.Contains("downloaded and verified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfflineCorruptionFailsWithoutNetworkFallback()
    {
        using var fixture = new Fixture();
        fixture.Options.DownloadModels = false;
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "test.bin"), "bad");
        using var store = fixture.Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureAvailableAsync(default));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task CanceledProvisionDoesNotDownload()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnsureAvailableAsync(cancellation.Token));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task RecognitionPreservesMarkdownAndReusesResultAndSession()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        var factory = new FakeFactory(_ => Task.FromResult("<x_0.1><y_0.2># Hello\n\nA **bold** paragraph.\n\n"
            + @"\begin{tabular}{cc} A & B\\ 1 & 2\\ \end{tabular}<class_Table>"));
        await using var recognizer = fixture.Recognizer(store, factory);
        await using var workspace = JobWorkspace.Create(fixture.Root, "test.pdf");
        workspace.OcrLanguage = "eslav"; // RapidOCR routing must not affect the multilingual VLM.
        await recognizer.RecognizeAsync(workspace, Index(), default);
        await recognizer.RecognizeAsync(workspace, Index(), default);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("image")));
        Assert.Equal("heading", result.RootElement[0].GetProperty("type").GetString());
        Assert.Contains("**bold**", result.RootElement[1].GetProperty("text").GetString());
        Assert.Equal("table", result.RootElement[2].GetProperty("type").GetString());
        Assert.Equal("1", result.RootElement[2].GetProperty("rows")[1][0].GetString());
        Assert.DoesNotContain("<x_", result.RootElement.GetRawText());
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, factory.Session.Calls);
        Assert.False(File.Exists(workspace.RecognitionErrorPath("image")));
    }

    [Fact]
    public async Task ImageFailureIsSanitizedAndDoesNotPublishPartialResult()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        var factory = new FakeFactory(_ => throw new InvalidDataException("private/model/path"));
        await using var recognizer = fixture.Recognizer(store, factory);
        await using var workspace = JobWorkspace.Create(fixture.Root, "test.pdf");
        await recognizer.RecognizeAsync(workspace, Index(), default);
        Assert.False(File.Exists(workspace.RecognitionPath("image")));
        var error = await File.ReadAllTextAsync(workspace.RecognitionErrorPath("image"));
        Assert.DoesNotContain("private", error);
        Assert.Contains("no external fallback", error);
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(NemotronExecutionMode.CpuInt8, false)]
    [InlineData(NemotronExecutionMode.WebGpuFp16, false)]
    [InlineData(NemotronExecutionMode.CpuInt8, true)]
    [InlineData(NemotronExecutionMode.WebGpuFp16, true)]
    public async Task SharedLayoutNormalizesEveryRegionAndNeverPublishesPartialResults(NemotronExecutionMode mode, bool failSecond)
    {
        using var fixture = new Fixture();
        fixture.Options.ExecutionMode = mode;
        using var store = fixture.Store();
        var layout = new RecognitionLayoutFixture();
        var calls = 0;
        var factory = new FakeFactory(_ =>
        {
            if (++calls == 2 && failSecond) throw new InvalidDataException("Test second-region failure");
            return Task.FromResult(calls == 1 ? "<x_0.1># Heading"
                : @"\begin{tabular}{cc} A & B\\ 1 & 2\\ \end{tabular}<class_Table>");
        });
        await using var recognizer = fixture.Recognizer(store, factory, layout.Pipeline());
        await using var workspace = JobWorkspace.Create(fixture.Root, "test.pdf");
        await layout.SaveImageAsync(workspace);
        await recognizer.RecognizeAsync(workspace, Index(), default);
        Assert.Equal(2, calls);
        Assert.Empty(Directory.GetFiles(workspace.Root, "recognition-crop-*.png"));
        if (failSecond)
        {
            Assert.False(File.Exists(workspace.RecognitionPath("image")));
            Assert.True(File.Exists(workspace.RecognitionErrorPath("image")));
            return;
        }
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("image")));
        Assert.Equal("heading", result.RootElement[0].GetProperty("type").GetString());
        Assert.Equal("Heading", result.RootElement[0].GetProperty("text").GetString());
        Assert.Equal("table", result.RootElement[1].GetProperty("type").GetString());
        Assert.Equal("2", result.RootElement[1].GetProperty("rows")[1][1].GetString());
        await recognizer.RecognizeAsync(workspace, Index(), default);
        Assert.Equal(2, calls);
        Assert.Equal(1, layout.Calls);
    }

    [Fact]
    public async Task CancellationPropagatesAndDisposalDrainsInFlightSession()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory(async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return ""; });
        var recognizer = fixture.Recognizer(store, factory);
        await using var workspace = JobWorkspace.Create(fixture.Root, "test.pdf");
        using var cancellation = new CancellationTokenSource();
        var work = recognizer.RecognizeAsync(workspace, Index(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var dispose = recognizer.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        await dispose;
        Assert.True(factory.Session.Disposed);
        Assert.False(File.Exists(workspace.RecognitionErrorPath("image")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => recognizer.RecognizeAsync(workspace, Index(), default));
        await recognizer.DisposeAsync();
    }

    [Fact]
    public void ResizeRespectsAspectRatioAndRejectsDecompressionBombs()
    {
        var options = new NemotronOptions();
        Assert.Equal((2048, 1024), NemotronImageProcessor.ResizeDimensions(4096, 2048, options));
        Assert.Equal((832, 1664), NemotronImageProcessor.ResizeDimensions(2000, 4000, options));
        Assert.Equal((31, 33), NemotronImageProcessor.ResizeDimensions(31, 33, options));
        Assert.Throws<InvalidDataException>(() => NemotronImageProcessor.ResizeDimensions(20000, 100, options));
        Assert.Throws<InvalidDataException>(() => NemotronImageProcessor.ResizeDimensions(10000, 10000, options));
    }

    [Fact]
    public void ImagePatchesUseChannelFirstClipNormalizationAndWhiteGridPadding()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "pixels.png");
        using (var bitmap = new SKBitmap(17, 19))
        {
            bitmap.Erase(SKColors.Red);
            using var output = File.Create(path);
            Assert.True(bitmap.Encode(output, SKEncodedImageFormat.Png, 100));
        }
        var image = NemotronImageProcessor.Prepare(path, fixture.Options, default);
        Assert.Equal(2, image.Rows);
        Assert.Equal(4, image.Columns);
        Assert.Equal((19, 17), (image.ContentHeight, image.ContentWidth));
        Assert.Equal((1f - 0.48145466f) / 0.26862954f, image.Patches[0, 0], 5);
        Assert.Equal(-0.4578275f / 0.26130258f, image.Patches[0, 256], 5);
        Assert.Equal((1f - 0.4578275f) / 0.26130258f, image.Patches[2, 256], 5);
    }

    [Theory]
    [InlineData(17, 19)]
    [InlineData(1, 19)]
    public void NavitConstantFieldRemainsConstantIncludingPadding(int height, int width)
    {
        var field = new float[128 * 128 * 1152];
        Array.Fill(field, 0.25f);
        var positions = new NavitPositions(field);
        var rows = (height + 15) / 16;
        var columns = (width + 63) / 64 * 4;
        var patches = new DenseTensor<float>([rows * columns, 1152]);
        patches.Fill(2f);
        var result = positions.Add(patches, new(new DenseTensor<float>([rows * columns, 768]), rows, columns, height, width), default);
        Assert.All(result.ToArray(), value => Assert.Equal(2.25f, value, 4));
    }

    [Fact]
    public void TokenizerDecodesUtf8AcrossTokenBoundariesAndLiteralAddedTokens()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "tokenizer.json");
        var vocabulary = new Dictionary<string, int>
        {
            ["</s>"] = 2, ["<s>"] = 0, ["<no_bbox>"] = 50005, ["<no_classes>"] = 50007,
            ["<output_markdown>"] = 50001, ["<predict_text_in_pic>"] = 50009,
            ["ĠHello"] = 100, ["Ð"] = 101, ["Ł"] = 102, ["Ġ"] = 103
        };
        var added = NemotronTokenizer.Prompt.Select(id => new
        {
            id = (int)id, content = vocabulary.Single(pair => pair.Value == id).Key, special = true
        }).Append(new { id = 52329, content = "Привет", special = false }).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            model = new { type = "BPE", vocab = vocabulary }, decoder = new { type = "ByteLevel" }, added_tokens = added
        }));
        var tokenizer = new NemotronTokenizer(path);
        Assert.Equal("Hello П Привет", tokenizer.Decode([0, 100, 103, 101, 102, 103, 52329, 2]));
        Assert.Throws<InvalidDataException>(() => tokenizer.Decode([999999]));
    }

    [Fact]
    public void GreedySelectionUsesLastPositionAndSignAwareRepetitionPenalty()
    {
        var logits = new DenseTensor<float>(new float[] { 100, 0, 0, 0, 5, 4.8f }, [1, 2, 3]);
        Assert.Equal(1, NemotronSession.SelectToken(logits, [], 1.1f));
        Assert.Equal(2, NemotronSession.SelectToken(logits, [1], 1.1f));
        logits[0, 1, 1] = -5;
        logits[0, 1, 2] = -5.2f;
        logits[0, 1, 0] = -10;
        Assert.Equal(2, NemotronSession.SelectToken(logits, [1], 1.1f));
    }

    private static DocumentIndex Index() => new()
    {
        SourceFileName = "test.pdf", Metadata = new DocumentMetadata { PageCount = 1 },
        Images = [new ImageManifestEntry { Hash = "image", RelativePath = "images/image.png", MediaType = "image/png" }]
    };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "flexyparse-nemotron-tests-" + Guid.NewGuid().ToString("N"));
        public NemotronOptions Options { get; } = new();
        public DownloadHandler Handler { get; } = new();
        public RecordingLogger<NemotronModelStore> Logger { get; } = new();
        public Fixture() { Directory.CreateDirectory(Root); Options.ModelsDirectory = Root; }
        public NemotronModelStore Store() => new(new Clients(Handler), Microsoft.Extensions.Options.Options.Create(Options),
            Logger,
            [new("test.bin", 3, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("abc"))))]);
        public NemotronImageRecognizer Recognizer(NemotronModelStore store, INemotronSessionFactory factory, ImageRecognitionPipeline? pipeline = null) =>
            new(store, factory, Microsoft.Extensions.Options.Options.Create(Options), new MarkdownJsonParser(), NullLogger<NemotronImageRecognizer>.Instance, pipeline);
        public void Dispose() { Handler.Dispose(); Directory.Delete(Root, recursive: true); }
    }

    private sealed class DownloadHandler : HttpMessageHandler
    {
        public byte[] Content { get; set; } = Encoding.UTF8.GetBytes("abc");
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            Assert.Contains(NemotronModelStore.Revision, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) });
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeFactory(Func<CancellationToken, Task<string>> recognize) : INemotronSessionFactory
    {
        public FakeSession Session { get; } = new(recognize);
        public int Calls { get; private set; }
        public INemotronSession Create(string modelsDirectory, NemotronOptions options) { Calls++; return Session; }
    }

    private sealed class FakeSession(Func<CancellationToken, Task<string>> recognize) : INemotronSession
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken) { Calls++; return recognize(cancellationToken); }
        public void Dispose() => Disposed = true;
    }
}

public sealed class NemotronNativeSmokeTests(ITestOutputHelper output)
{
    [NemotronGpuFact]
    public async Task PinnedFp16PackRecognizesOnWebGpuOffline()
    {
        var options = new NemotronOptions
        {
            ExecutionMode = NemotronExecutionMode.WebGpuFp16,
            ModelsDirectory = Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_NEMOTRON_GPU_CACHE")!
        };
        using (var settings = new SessionOptions()) NemotronSession.ConfigureExecutionProvider(settings, options);
        await RecognizeHelloWorldAsync(options);
    }

    [NemotronNativeFact]
    public async Task PinnedInt8PackDownloadsAndRecognizesHelloWorldOffline()
    {
        var options = new NemotronOptions
        {
            ModelsDirectory = Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_NEMOTRON_CACHE")!
        };
        await RecognizeHelloWorldAsync(options);
    }

    private async Task RecognizeHelloWorldAsync(NemotronOptions options)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(NemotronModelStore.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var store = new NemotronModelStore(provider.GetRequiredService<IHttpClientFactory>(),
            Microsoft.Extensions.Options.Options.Create(options), NullLogger<NemotronModelStore>.Instance);
        var directory = await store.EnsureAvailableAsync(cancellation.Token);
        options.DownloadModels = false;
        using var offline = new NemotronModelStore(provider.GetRequiredService<IHttpClientFactory>(),
            Microsoft.Extensions.Options.Options.Create(options), NullLogger<NemotronModelStore>.Instance);
        Assert.Equal(directory, await offline.EnsureAvailableAsync(cancellation.Token));

        await using var workspace = JobWorkspace.Create(Path.GetTempPath(), "nemotron-native.pdf");
        var imagePath = Path.Combine(workspace.ImagesDirectory, "hello.png");
        using (var bitmap = new SKBitmap(640, 160))
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(SKTypeface.Default, 48))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText("Hello World", 40, 95, SKTextAlign.Left, font, paint);
            using var stream = File.Create(imagePath);
            Assert.True(bitmap.Encode(stream, SKEncodedImageFormat.Png, 100));
        }
        using var session = new NemotronSessionFactory().Create(directory, options);
        var text = await session.RecognizeAsync(imagePath, cancellation.Token);
        output.WriteLine($"Real {options.ExecutionMode} OCR: {text}");
        Assert.Contains("Hello", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("World", text, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class NemotronGpuFactAttribute : FactAttribute
{
    public NemotronGpuFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_NEMOTRON_GPU_CACHE")))
            Skip = "Set FLEXYPARSE_TEST_NEMOTRON_GPU_CACHE to allow ~1.45 GB download and real WebGPU inference.";
    }
}

public sealed class NemotronNativeFactAttribute : FactAttribute
{
    public NemotronNativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_NEMOTRON_CACHE")))
            Skip = "Set FLEXYPARSE_TEST_NEMOTRON_CACHE to allow the pinned ~761 MB download and real CPU inference.";
    }
}
