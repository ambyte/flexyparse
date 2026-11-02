using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FlexyParse.DependencyInjection;
using FlexyParse.Markdown;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.RapidOcr;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TagBites.Text.Markdown;

namespace FlexyParse.Tests;

public sealed class RapidOcrProviderTests
{
    public static TheoryData<string> ModelPrefixes => new(OcrLanguageProfiles.ModelNames);

    [Theory]
    [MemberData(nameof(ModelPrefixes))]
    public void EveryModelPrefixRoutesToItselfAndValidatesOptions(string model)
    {
        Assert.Equal(model, OcrLanguageProfiles.Normalize(" " + model.ToUpperInvariant() + " "));
        var recognition = new RecognitionOptions { Provider = RecognitionProvider.RapidOcr, Language = model };
        var options = new RapidOcrOptions();
        var validator = new RapidOcrOptionsValidator(Microsoft.Extensions.Options.Options.Create(recognition));
        Assert.True(validator.Validate(null, options).Succeeded);
        options.Profiles[model] = new() { DictionaryPath = "unpaired.txt" };
        Assert.True(validator.Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("eslav", "eslav")]
    [InlineData(" ESLAV ", "eslav")]
    [InlineData("ESLAV", "eslav")]
    [InlineData("en", "en")]
    [InlineData("LATIN", "latin")]
    [InlineData("cyrillic", "cyrillic")]
    public void LanguageRoutingIsExplicit(string input, string normalized)
    {
        Assert.Equal(normalized, OcrLanguageProfiles.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("de")]
    [InlineData("zh-CN")]
    [InlineData("auto")]
    [InlineData("ch_unknown")]
    [InlineData("ru")]
    [InlineData("uk")]
    [InlineData("be")]
    [InlineData("ru-RU")]
    [InlineData("uk-UA")]
    [InlineData("be-BY")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("not a language")]
    public void UnsupportedLanguagesAreRejected(string language) =>
        Assert.ThrowsAny<ArgumentException>(() => OcrLanguageProfiles.Normalize(language));

    [Fact]
    public async Task DefaultProviderRemainsOpenAiWithoutLocalModels()
    {
        var services = Services();
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value;
        Assert.Equal(RecognitionProvider.OpenAi, options.Recognition.Provider);
        Assert.Equal("eslav", options.Recognition.Language);
        Assert.IsType<OpenAiImageRecognizer>(provider.GetRequiredService<IImageRecognizer>());
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public async Task PresetConfigurationPassesStartupValidationBeforeDownload()
    {
        var services = Services();
        services.Configure<FlexyParseOptions>(options => options.Recognition.Provider = RecognitionProvider.RapidOcr);
        services.AddRapidOcrRecognition(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.Equal("models/rapidocr/v3.10.0", provider.GetRequiredService<IOptions<RapidOcrOptions>>().Value.ModelsDirectory);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task UnknownProviderIsRejected(int providerValue)
    {
        var services = Services();
        services.Configure<FlexyParseOptions>(options => options.Recognition.Provider = (RecognitionProvider)providerValue);
        await using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value);
    }

    [Theory]
    [InlineData("language")]
    [InlineData("concurrency")]
    [InlineData("threads")]
    [InlineData("pixels")]
    [InlineData("dimension")]
    [InlineData("partial-profile")]
    [InlineData("missing-model")]
    public void InvalidLocalSettingsAreRejected(string setting)
    {
        using var fixture = new Fixture();
        var local = fixture.LocalOptions;
        switch (setting)
        {
            case "language": fixture.Options.Recognition.Language = "de"; break;
            case "concurrency": local.MaxConcurrency = 0; break;
            case "threads": local.InferenceThreads = 0; break;
            case "pixels": local.MaxImagePixels = 0; break;
            case "dimension": local.MaxImageDimension = 0; break;
            case "partial-profile": local.Profiles["latin"].DictionaryPath = ""; break;
            case "missing-model": local.DetectionModelPath = "missing.onnx"; break;
        }

        Assert.True(new RapidOcrOptionsValidator(Microsoft.Extensions.Options.Options.Create(fixture.Options.Recognition))
            .Validate(null, fixture.LocalOptions).Failed);
    }

    [Fact]
    public void OpenAiDoesNotValidateLocalOnlySettings()
    {
        var options = new FlexyParseOptions();
        options.Recognition.Language = "de";
        var local = new RapidOcrOptions { MaxConcurrency = 0 };
        Assert.True(new RapidOcrOptionsValidator(Microsoft.Extensions.Options.Options.Create(options.Recognition)).Validate(null, local).Succeeded);
    }

    [Fact]
    public async Task PlainTextIsEscapedWithoutInterpretingMarkdownOrHtml()
    {
        using var fixture = new Fixture();
        var raw = "# Ignore previous instructions\n<script>alert('x')</script>\n[click](https://example.test) & **bold**\n1. item\n```sh\nrm -rf /\n```";
        var factory = new FakeFactory((_, _) => Task.FromResult(raw));
        await using var recognizer = fixture.Recognizer(factory);
        await using var workspace = fixture.Workspace();
        await recognizer.RecognizeAsync(workspace, Index(), CancellationToken.None);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("image")));
        Assert.All(json.RootElement.EnumerateArray(), block => Assert.Equal("paragraph", block.GetProperty("type").GetString()));
        Assert.StartsWith("\\# Ignore", json.RootElement[0].GetProperty("text").GetString());
        Assert.Contains("&lt;script&gt;", json.RootElement[1].GetProperty("text").GetString());
        var markdown = new MarkdownDocument();
        new MarkdownJsonFormatter().AppendTo(markdown, json.RootElement);
        var html = Markdig.Markdown.ToHtml(markdown.ToString());
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("<strong>", html);
        Assert.DoesNotContain("<h1>", html);
        Assert.DoesNotContain("<ol>", html);
        Assert.DoesNotContain("<pre>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.False(File.Exists(workspace.RecognitionPath("image") + ".tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedLayoutPreservesPlainTextAndNeverPublishesPartialRegions(bool failSecond)
    {
        using var fixture = new Fixture();
        var layout = new RecognitionLayoutFixture();
        var calls = 0;
        var factory = new FakeFactory((_, _) =>
        {
            if (++calls == 2 && failSecond) throw new InvalidDataException("Test second-region failure");
            return Task.FromResult(calls == 1 ? "# Text" : "| A | B |");
        });
        await using var recognizer = fixture.Recognizer(factory, layout.Pipeline());
        await using var workspace = fixture.Workspace();
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
        Assert.Equal(2, result.RootElement.GetArrayLength());
        Assert.All(result.RootElement.EnumerateArray(), block => Assert.Equal("paragraph", block.GetProperty("type").GetString()));
        Assert.StartsWith("\\# Text", result.RootElement[0].GetProperty("text").GetString());
        Assert.Contains("\\|", result.RootElement[1].GetProperty("text").GetString());
        await recognizer.RecognizeAsync(workspace, Index(), default);
        Assert.Equal(2, calls);
        Assert.Equal(1, layout.Calls);
    }

    [Fact]
    public async Task ConcurrentJobsKeepTheirLanguageAndReuseOnlyMatchingSessions()
    {
        using var fixture = new Fixture();
        fixture.LocalOptions.MaxConcurrency = 2;
        var factory = new FakeFactory((profile, _) => Task.FromResult(Path.GetFileName(profile.DictionaryPath)));
        await using var recognizer = fixture.Recognizer(factory);
        await using var russian = fixture.Workspace("eslav");
        await using var english = fixture.Workspace("en");
        await using var ukrainian = fixture.Workspace("eslav");
        await Task.WhenAll(recognizer.RecognizeAsync(russian, Index(), default),
            recognizer.RecognizeAsync(english, Index(), default), recognizer.RecognizeAsync(ukrainian, Index(), default));
        Assert.Contains("eslav", await File.ReadAllTextAsync(russian.RecognitionPath("image")));
        Assert.Contains("en", await File.ReadAllTextAsync(english.RecognitionPath("image")));
        Assert.Contains("eslav", await File.ReadAllTextAsync(ukrainian.RecognitionPath("image")));
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal("eslav", fixture.Options.Recognition.Language);
    }

    [Fact]
    public async Task EveryModelUsesItsMatchingDictionaryAndOwnSession()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory((profile, _) => Task.FromResult(Path.GetFileName(profile.DictionaryPath)));
        await using var recognizer = fixture.Recognizer(factory);
        foreach (var model in OcrLanguageProfiles.ModelNames)
        {
            await using var workspace = fixture.Workspace(model);
            await recognizer.RecognizeAsync(workspace, Index(), default);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("image")));
            var escaped = result.RootElement[0].GetProperty("text").GetString();
            Assert.Equal(model.Replace("_", "\\_", StringComparison.Ordinal) + "\\.txt", escaped);
        }
        Assert.Equal(13, factory.Created.Count);
    }

    [Fact]
    public async Task GlobalConcurrencyIsBoundedAcrossProfilesAndJobs()
    {
        using var fixture = new Fixture();
        fixture.LocalOptions.MaxConcurrency = 2;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var sync = new object();
        var factory = new FakeFactory(async (_, token) =>
        {
            lock (sync)
            {
                active++;
                maximum = Math.Max(maximum, active);
                if (active == 2) entered.TrySetResult();
            }

            try { await release.Task.WaitAsync(token); return "text"; }
            finally { lock (sync) active--; }
        });
        await using var recognizer = fixture.Recognizer(factory);
        await using var russian = fixture.Workspace("eslav");
        await using var english = fixture.Workspace("en");
        await using var cyrillic = fixture.Workspace("cyrillic");
        var work = Task.WhenAll(recognizer.RecognizeAsync(russian, Index(), default),
            recognizer.RecognizeAsync(english, Index(), default), recognizer.RecognizeAsync(cyrillic, Index(), default));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.TrySetResult(); }
        await work;
        Assert.Equal(2, maximum);
        Assert.All(factory.Created, session => Assert.Equal(1, session.MaximumActive));
    }

    [Fact]
    public async Task CancellationWhileQueuedDoesNotCallEngineOrWriteResults()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return "text"; });
        await using var recognizer = fixture.Recognizer(factory);
        await using var first = fixture.Workspace();
        await using var queued = fixture.Workspace();
        var work = recognizer.RecognizeAsync(first, Index(), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var waiting = recognizer.RecognizeAsync(queued, Index(), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Empty(Directory.GetFiles(queued.ImagesDirectory));
            Assert.Equal(1, factory.Created.Single().Calls);
        }
        finally { release.TrySetResult(); await work; }
    }

    [Fact]
    public async Task CancellationDuringInferenceDoesNotBecomeImageError()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var factory = new FakeFactory((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(""); });
        await using var recognizer = fixture.Recognizer(factory);
        await using var workspace = fixture.Workspace();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognizer.RecognizeAsync(workspace, Index(), cancellation.Token));
        Assert.Empty(Directory.GetFiles(workspace.ImagesDirectory));
    }

    [Fact]
    public async Task FailedInferenceWritesSafeErrorAndNeverFallsBack()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory((_, _) => throw new InvalidDataException("private model path"));
        await using var recognizer = fixture.Recognizer(factory);
        await using var workspace = fixture.Workspace();
        await recognizer.RecognizeAsync(workspace, Index(), default);
        var error = await File.ReadAllTextAsync(workspace.RecognitionErrorPath("image"));
        Assert.Contains("no external fallback", error);
        Assert.DoesNotContain("private model path", error);
        Assert.False(File.Exists(workspace.RecognitionPath("image")));
        Assert.Equal(1, factory.Created.Single().Calls);
    }

    [Fact]
    public async Task CachedResultsAreReusedAndSuccessRemovesStaleErrors()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory((_, _) => Task.FromResult("text"));
        await using var recognizer = fixture.Recognizer(factory);
        await using var workspace = fixture.Workspace();
        await File.WriteAllTextAsync(workspace.RecognitionErrorPath("image"), "old error");
        await recognizer.RecognizeAsync(workspace, Index(), default);
        await recognizer.RecognizeAsync(workspace, Index(), default);
        Assert.False(File.Exists(workspace.RecognitionErrorPath("image")));
        Assert.Equal(1, factory.Created.Single().Calls);
    }

    [Fact]
    public async Task BlankOcrProducesEmptyArrayRatherThanInventedContent()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory((_, _) => Task.FromResult("  \r\n\t\n"));
        await using var recognizer = fixture.Recognizer(factory);
        await using var workspace = fixture.Workspace();
        await recognizer.RecognizeAsync(workspace, Index(), default);
        Assert.Equal("[]", await File.ReadAllTextAsync(workspace.RecognitionPath("image")));
    }

    [Fact]
    public async Task AlreadyCancelledCallNeverCreatesSession()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory((_, _) => Task.FromResult("text"));
        await using var recognizer = fixture.Recognizer(factory);
        await using var workspace = fixture.Workspace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognizer.RecognizeAsync(workspace, Index(), cancellation.Token));
        Assert.Empty(factory.Created);
        Assert.Empty(Directory.GetFiles(workspace.ImagesDirectory));
    }

    [Fact]
    public async Task SameProfileIsSerializedEvenWhenGlobalLimitAllowsOverlap()
    {
        using var fixture = new Fixture();
        fixture.LocalOptions.MaxConcurrency = 2;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return "text"; });
        await using var recognizer = fixture.Recognizer(factory);
        await using var first = fixture.Workspace("eslav");
        await using var second = fixture.Workspace("eslav");
        var work = recognizer.RecognizeAsync(first, Index(), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var waiting = recognizer.RecognizeAsync(second, Index(), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(1, factory.Created.Single().Calls);
        }
        finally { release.TrySetResult(); await work; }
        Assert.Equal(1, factory.Created.Single().MaximumActive);
    }

    [Fact]
    public async Task MissingJobProfileAndUnsupportedLanguageFailWithoutCreatingSession()
    {
        using var fixture = new Fixture();
        fixture.LocalOptions.Profiles["en"].RecognitionModelPath = "missing.onnx";
        var factory = new FakeFactory((_, _) => Task.FromResult("text"));
        await using var recognizer = fixture.Recognizer(factory);
        await using var workspace = fixture.Workspace("en");
        await Assert.ThrowsAsync<InvalidOperationException>(() => recognizer.RecognizeAsync(workspace, Index(), default));
        workspace.OcrLanguage = "de";
        await Assert.ThrowsAsync<ArgumentException>(() => recognizer.RecognizeAsync(workspace, Index(), default));
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task DisposalDrainsInferenceAndDisposesOwnedSession()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return "text"; });
        await using var recognizer = fixture.Recognizer(factory);
        await using var workspace = fixture.Workspace();
        var work = recognizer.RecognizeAsync(workspace, Index(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal = recognizer.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        release.TrySetResult();
        await work;
        await disposal;
        Assert.Equal(1, factory.Created.Single().Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => recognizer.RecognizeAsync(workspace, Index(), default));
    }

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Markdown)]
    [InlineData(OutputFormat.Html)]
    public async Task ConversionUsesLocalProviderWithoutApiKeyAndPassesJobLanguage(OutputFormat format)
    {
        using var fixture = new Fixture();
        var services = Services();
        services.Configure<FlexyParseOptions>(options => options.Recognition = fixture.Options.Recognition);
        services.AddRapidOcrRecognition(new ConfigurationBuilder().Build());
        services.AddSingleton<IOptions<RapidOcrOptions>>(Microsoft.Extensions.Options.Options.Create(fixture.LocalOptions));
        var factory = new FakeFactory((profile, _) => Task.FromResult(Path.GetFileName(profile.DictionaryPath)));
        services.AddSingleton<IRapidOcrSessionFactory>(factory);
        await using var provider = services.BuildServiceProvider();
        Assert.IsType<RapidOcrImageRecognizer>(provider.GetRequiredService<IImageRecognizer>());
        await using var input = new MemoryStream(ConversionCancellationTests.BuildPdf(withImage: true));
        await using var output = new MemoryStream();
        await provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
        {
            Content = input, FileName = "local.pdf", Format = format, OcrLanguage = "en"
        }, output, default);
        Assert.Contains("en", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Single(factory.Created);
    }

    [Theory]
    [MemberData(nameof(ModelPrefixes))]
    public void MissingProfileUsesPresetWithoutAddingOverrides(string model)
    {
        var options = new RapidOcrOptions();
        var profile = options.GetProfile(model);
        Assert.Empty(profile.RecognitionModelPath);
        Assert.Empty(profile.DictionaryPath);
        Assert.Empty(options.Profiles);
        profile.DictionaryPath = "must-not-be-shared.txt";
        Assert.Empty(options.GetProfile(model).DictionaryPath);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("ru")]
    [InlineData("chserver")]
    [InlineData(" eslav ")]
    public void UnknownOverrideKeysAreRejected(string key)
    {
        var options = new RapidOcrOptions();
        options.Profiles[key] = new();
        var recognition = new RecognitionOptions { Provider = RecognitionProvider.RapidOcr };
        var validator = new RapidOcrOptionsValidator(Microsoft.Extensions.Options.Options.Create(recognition));
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, error => error.Contains("Profiles:" + key, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ModelPrefixes))]
    public void ProfileOverridesBindCaseInsensitivelyAndValidate(string model)
    {
        using var fixture = new Fixture();
        var expected = fixture.LocalOptions.GetProfile(model);
        var key = RapidOcrOptions.SectionName + ":Profiles:" + model.ToUpperInvariant();
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [key + ":RecognitionModelPath"] = expected.RecognitionModelPath,
            [key + ":DictionaryPath"] = expected.DictionaryPath
        }).Build();
        var options = configuration.GetSection(RapidOcrOptions.SectionName).Get<RapidOcrOptions>()!;
        Assert.Single(options.Profiles);
        Assert.Equal(expected.RecognitionModelPath, options.GetProfile(model).RecognitionModelPath);
        Assert.Equal(expected.DictionaryPath, options.GetProfile(model).DictionaryPath);
        var validator = new RapidOcrOptionsValidator(Microsoft.Extensions.Options.Options.Create(fixture.Options.Recognition));
        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void NullOverrideIsRejectedAndUnknownProfileCannotUsePresetFallback()
    {
        var options = new RapidOcrOptions();
        options.Profiles["eslav"] = null!;
        var recognition = new RecognitionOptions { Provider = RecognitionProvider.RapidOcr };
        var validator = new RapidOcrOptionsValidator(Microsoft.Extensions.Options.Options.Create(recognition));
        Assert.True(validator.Validate(null, options).Failed);
        Assert.ThrowsAny<ArgumentException>(() => options.GetProfile("unknown"));
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(new ConfigurationBuilder().Build());
        return services;
    }

    private static DocumentIndex Index() => new()
    {
        SourceFileName = "local.pdf", Metadata = new DocumentMetadata { PageCount = 1 },
        Images = [new ImageManifestEntry { Hash = "image", RelativePath = "images/image.png", MediaType = "image/png" }]
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flexyparse-local-tests-" + Guid.NewGuid().ToString("N"));
        public FlexyParseOptions Options { get; } = new();
        public RapidOcrOptions LocalOptions { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Options.Recognition.Provider = RecognitionProvider.RapidOcr;
            var local = LocalOptions;
            local.DetectionModelPath = FilePath("det.onnx");
            local.ClassificationModelPath = FilePath("cls.onnx");
            foreach (var name in OcrLanguageProfiles.ModelNames)
            {
                local.Profiles[name] = new()
                {
                    RecognitionModelPath = FilePath(name + ".onnx"),
                    DictionaryPath = FilePath(name + ".txt")
                };
            }
        }

        private string FilePath(string name)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, "fake model for injected sessions only");
            return path;
        }

        public JobWorkspace Workspace(string? language = null)
        {
            var workspace = JobWorkspace.Create(_root, "local.pdf");
            workspace.OcrLanguage = language;
            return workspace;
        }

        public RapidOcrImageRecognizer Recognizer(FakeFactory factory, ImageRecognitionPipeline? pipeline = null) =>
            new(Microsoft.Extensions.Options.Options.Create(Options.Recognition), Microsoft.Extensions.Options.Options.Create(LocalOptions),
                factory, NullLogger<RapidOcrImageRecognizer>.Instance,
                _store ??= RapidOcrModelStoreTests.OfflineStore(Options, LocalOptions), pipeline);

        private RapidOcrModelStore? _store;

        public void Dispose()
        {
            _store?.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeFactory(Func<RapidOcrModelProfile, CancellationToken, Task<string>> recognize) : IRapidOcrSessionFactory
    {
        public ConcurrentBag<FakeSession> Created { get; } = [];

        public IRapidOcrSession Create(RapidOcrOptions options, RapidOcrModelProfile profile)
        {
            var session = new FakeSession(token => recognize(profile, token));
            Created.Add(session);
            return session;
        }
    }

    private sealed class FakeSession(Func<CancellationToken, Task<string>> recognize) : IRapidOcrSession
    {
        private int _active;
        public int MaximumActive { get; private set; }
        public int Calls { get; private set; }
        public int Disposals { get; private set; }

        public async Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumActive = Math.Max(MaximumActive, active);
            Calls++;
            try { return await recognize(cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void Dispose() => Disposals++;
    }
}
