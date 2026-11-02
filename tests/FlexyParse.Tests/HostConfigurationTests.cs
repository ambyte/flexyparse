using System.Text;
using FlexyParse.Hosting;
using FlexyParse.Nemotron;
using FlexyParse.Options;
using FlexyParse.RapidOcr;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlexyParse.Tests;

public sealed class HostConfigurationTests
{
    [Fact]
    public void RenamedRapidOcrLanguageAliasAcceptsAllModelPrefixesAndIgnoresOldAlias()
    {
        foreach (var model in FlexyParse.Recognition.OcrLanguageProfiles.ModelNames)
        {
            using var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FlexyParse:Recognition:Language"] = "configured" });
            HostConfiguration.Apply(configuration, name => name == "FLEXYPARSE_OCR_LANGUAGE" ? "obsolete" : null);
            Assert.Equal("configured", configuration["FlexyParse:Recognition:Language"]);
            HostConfiguration.Apply(configuration, name => name switch
            {
                "FLEXYPARSE_RAPIDOCR_LANGUAGE" => model,
                "FLEXYPARSE_OCR_LANGUAGE" => "obsolete",
                _ => null
            });
            Assert.Equal(model, configuration["FlexyParse:Recognition:Language"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WebStartupWithUrlsKeepsAliasesAboveJsonDefaults(bool explicitModelKey)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flexyparse-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.json"),
                """{"FlexyParse":{"ModelApi":{"ApiKey":""},"MaxConcurrentDocuments":2}}""");
            var args = new List<string> { "--urls", "http://localhost:8088" };
            if (explicitModelKey)
            {
                args.Add("--FlexyParse:ModelApi:ApiKey=command-line-key");
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args.ToArray(),
                ContentRootPath = directory,
                EnvironmentName = "Production"
            });
            using var configuration = builder.Configuration;
            HostConfiguration.Apply(configuration, name => name switch
            {
                "FLEXYPARSE_MODEL_API_KEY" => "test-model-key",
                "FLEXYPARSE_MAX_CONCURRENT_DOCUMENTS" => "5",
                _ => null
            });

            Assert.Equal(explicitModelKey ? "command-line-key" : "test-model-key",
                configuration["FlexyParse:ModelApi:ApiKey"]);
            Assert.Equal("5", configuration["FlexyParse:MaxConcurrentDocuments"]);
            Assert.Equal("http://localhost:8088", configuration["urls"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("FLEXYPARSE_API_KEY", "Host:ApiKey")]
    [InlineData("FLEXYPARSE_MAX_CONCURRENT_DOCUMENTS", "MaxConcurrentDocuments")]
    [InlineData("FLEXYPARSE_MAX_CONCURRENT_IMAGE_RECOGNITIONS", "MaxConcurrentImageRecognitions")]
    [InlineData("FLEXYPARSE_MAX_UPLOAD_BYTES", "MaxUploadBytes")]
    [InlineData("FLEXYPARSE_MAX_FILES_PER_REQUEST", "MaxFilesPerRequest")]
    [InlineData("FLEXYPARSE_RECOGNIZE_IMAGES", "RecognizeImages")]
    [InlineData("FLEXYPARSE_RECOGNITION_PROVIDER", "Recognition:Provider")]
    [InlineData("FLEXYPARSE_NEMOTRON_GPU_DEVICE_ID", "Recognition:Nemotron:GpuDeviceId")]
    [InlineData("FLEXYPARSE_NEMOTRON_INFERENCE_THREADS", "Recognition:Nemotron:InferenceThreads")]
    [InlineData("FLEXYPARSE_RAPIDOCR_LANGUAGE", "Recognition:Language")]
    [InlineData("FLEXYPARSE_KEEP_RESULTS", "KeepResults")]
    [InlineData("FLEXYPARSE_IMAGE_LAYOUT_ENABLED", "ImageLayout:Enabled")]
    [InlineData("FLEXYPARSE_GOTENBERG_URL", "GotenbergUrl")]
    [InlineData("FLEXYPARSE_GOTENBERG_TIMEOUT_SECONDS", "GotenbergTimeoutSeconds")]
    [InlineData("FLEXYPARSE_ASYNC_WORKER_COUNT", "Async:WorkerCount")]
    [InlineData("FLEXYPARSE_ASYNC_QUEUE_CAPACITY", "Async:QueueCapacity")]
    [InlineData("FLEXYPARSE_ASYNC_MAX_STORED_TASKS", "Async:MaxStoredTasks")]
    [InlineData("FLEXYPARSE_ASYNC_RESULT_RETENTION_SECONDS", "Async:ResultRetentionSeconds")]
    [InlineData("FLEXYPARSE_ASYNC_CLEANUP_INTERVAL_SECONDS", "Async:CleanupIntervalSeconds")]
    [InlineData("FLEXYPARSE_ASYNC_MAX_WEBSOCKET_CONNECTIONS", "Async:MaxWebSocketConnections")]
    [InlineData("FLEXYPARSE_MODEL_API_KEY", "ModelApi:ApiKey")]
    [InlineData("FLEXYPARSE_MODEL_API_BASE_URL", "ModelApi:BaseUrl")]
    [InlineData("FLEXYPARSE_MODEL_API_REQUIRE_API_KEY", "ModelApi:RequireApiKey")]
    [InlineData("FLEXYPARSE_MODEL_API_MODEL", "ModelApi:Model")]
    [InlineData("FLEXYPARSE_MODEL_API_TIMEOUT_SECONDS", "ModelApi:TimeoutSeconds")]
    [InlineData("FLEXYPARSE_MODEL_API_MAX_ATTEMPTS", "ModelApi:MaxAttempts")]
    [InlineData("FLEXYPARSE_MODEL_API_MAX_COMPLETION_TOKENS", "ModelApi:MaxCompletionTokens")]
    [InlineData("FLEXYPARSE_MODEL_API_TOKEN_LIMIT_PARAMETER", "ModelApi:TokenLimitParameter")]
    [InlineData("FLEXYPARSE_MODEL_API_TEMPERATURE", "ModelApi:Temperature")]
    [InlineData("FLEXYPARSE_MODEL_API_MAX_IMAGE_BYTES", "ModelApi:MaxImageBytes")]
    [InlineData("FLEXYPARSE_MODEL_API_TITLE", "ModelApi:Title")]
    [InlineData("FLEXYPARSE_MODEL_API_HTTP_REFERER", "ModelApi:HttpReferer")]
    public void AliasOverridesConfiguredDefault(string variable, string key)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FlexyParse:" + key] = "default" });

        HostConfiguration.Apply(configuration, name => name == variable ? "override" : null);

        Assert.Equal("override", configuration["FlexyParse:" + key]);
    }

    [Theory]
    [InlineData("MODEL_PATH", "ModelPath")]
    [InlineData("CONFIDENCE", "Confidence")]
    [InlineData("IOU", "IoU")]
    [InlineData("IMAGE_RESIZE", "ImageResize")]
    [InlineData("MAX_REGIONS", "MaxRegions")]
    [InlineData("MAX_PIXELS", "MaxPixels")]
    [InlineData("PADDING_PIXELS", "PaddingPixels")]
    [InlineData("MIN_CONTENT_COVERAGE", "MinContentCoverage")]
    public void RemovedLayoutAliasesDoNotOverridePresets(string suffix, string key)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:ImageLayout:" + key] = "preset"
        });

        HostConfiguration.Apply(configuration, name => name == "FLEXYPARSE_IMAGE_LAYOUT_" + suffix ? "override" : null);

        Assert.Equal("preset", configuration["FlexyParse:ImageLayout:" + key]);
    }

    [Fact]
    public void MissingAliasesPreserveJsonDefaults()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("""{"FlexyParse":{"MaxConcurrentDocuments":2}}"""));
        using var defaults = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(json).Build();
        using var configuration = new ConfigurationManager();
        configuration.AddConfiguration(defaults);

        HostConfiguration.Apply(configuration, _ => null);

        Assert.Equal("2", configuration["FlexyParse:MaxConcurrentDocuments"]);
    }

    [Theory]
    [InlineData("FLEXYPARSE_GOTENBERG_URL", "GotenbergUrl")]
    [InlineData("FLEXYPARSE_MODEL_API_KEY", "ModelApi:ApiKey")]
    [InlineData("FLEXYPARSE_MODEL_API_TEMPERATURE", "ModelApi:Temperature")]
    [InlineData("FLEXYPARSE_API_KEY", "Host:ApiKey")]
    public void ExplicitEmptyAliasClearsLowerPriorityValue(string variable, string key)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FlexyParse:" + key] = "default" });

        HostConfiguration.Apply(configuration, name => name == variable ? "" : null);

        Assert.Equal("", configuration["FlexyParse:" + key]);
    }

    [Theory]
    [InlineData(null, "legacy-key")]
    [InlineData("alias-key", "alias-key")]
    [InlineData("", "")]
    public void LegacyModelKeyHasLowerPriorityThanExplicitAlias(string? alias, string expected)
    {
        using var configuration = new ConfigurationManager();
        HostConfiguration.Apply(configuration, name => name switch
        {
            "MODEL_API_KEY" => "legacy-key",
            "FLEXYPARSE_MODEL_API_KEY" => alias,
            _ => null
        });

        Assert.Equal(expected, configuration["FlexyParse:ModelApi:ApiKey"]);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("")]
    public void StandardEnvironmentLayerWinsEvenWhenEmpty(string standardValue)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FlexyParse:Host:ApiKey"] = "default" });
        var prefixedSource = new EnvironmentVariablesConfigurationSource { Prefix = Guid.NewGuid().ToString("N") };
        var standardSource = new EnvironmentVariablesConfigurationSource();
        configuration.Sources.Add(prefixedSource);
        configuration.Sources.Add(standardSource);
        // Model the standard value without mutating shared process environment variables.
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FlexyParse:Host:ApiKey"] = standardValue });

        HostConfiguration.Apply(configuration, name => name == "FLEXYPARSE_API_KEY" ? "alias-key" : null);

        Assert.Equal(standardValue, configuration["FlexyParse:Host:ApiKey"]);
        Assert.Equal(configuration.Sources.IndexOf(prefixedSource) + 2, configuration.Sources.IndexOf(standardSource));
    }

    [Fact]
    public void CommandLineHasHighestPriority()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddCommandLine(["--FlexyParse:ModelApi:Model=command-line-model"]);

        HostConfiguration.Apply(configuration, name => name == "FLEXYPARSE_MODEL_API_MODEL" ? "alias-model" : null);

        Assert.Equal("command-line-model", configuration["FlexyParse:ModelApi:Model"]);
    }

    [Fact]
    public void EmptyTemperatureBindsToNullAndAliasLimitsAreValidated()
    {
        using var configuration = new ConfigurationManager();
        HostConfiguration.Apply(configuration, name => name switch
        {
            "FLEXYPARSE_MODEL_API_TEMPERATURE" => "",
            "FLEXYPARSE_ASYNC_WORKER_COUNT" => "0",
            _ => null
        });

        Assert.Null(configuration.GetSection("FlexyParse").Get<FlexyParseOptions>()!.ModelApi.Temperature);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        ConversionTaskService.AddServices(services);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AsyncConversionOptions>>().Value);
    }

    [Fact]
    public void RecognitionAliasesBindProviderAndLanguageWithoutModelPaths()
    {
        using var configuration = new ConfigurationManager();
        HostConfiguration.Apply(configuration, name => name switch
        {
            "FLEXYPARSE_RECOGNITION_PROVIDER" => "RapidOcr",
            "FLEXYPARSE_RAPIDOCR_LANGUAGE" => "eslav",
            _ => null
        });

        var options = configuration.GetSection("FlexyParse").Get<FlexyParseOptions>()!;
        Assert.Equal(RecognitionProvider.RapidOcr, options.Recognition.Provider);
        Assert.Equal("eslav", options.Recognition.Language);
        var local = configuration.GetSection(RapidOcrOptions.SectionName).Get<RapidOcrOptions>() ?? new();
        Assert.Empty(local.DetectionModelPath);
        Assert.Empty(local.Profiles);
    }

    [Theory]
    [InlineData("NemotronCPU", NemotronExecutionMode.CpuInt8)]
    [InlineData("NemotronGPU", NemotronExecutionMode.WebGpuFp16)]
    public async Task NemotronAliasesSelectAutomaticModeAndGpuDevice(string name, NemotronExecutionMode mode)
    {
        using var configuration = new ConfigurationManager();
        HostConfiguration.Apply(configuration, variable => variable switch
        {
            "FLEXYPARSE_RECOGNITION_PROVIDER" => name,
            "FLEXYPARSE_NEMOTRON_GPU_DEVICE_ID" => "2",
            "FLEXYPARSE_NEMOTRON_INFERENCE_THREADS" => "8",
            _ => null
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredFlexyParse(configuration);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NemotronOptions>>().Value;
        Assert.Equal(mode, options.ExecutionMode);
        Assert.Equal(2, options.GpuDeviceId);
        Assert.Equal(8, options.InferenceThreads);
    }

    [Theory]
    [InlineData("GpuDeviceId", "FLEXYPARSE_NEMOTRON_GPU_DEVICE_ID", false, "3")]
    [InlineData("GpuDeviceId", "FLEXYPARSE_NEMOTRON_GPU_DEVICE_ID", true, "4")]
    [InlineData("InferenceThreads", "FLEXYPARSE_NEMOTRON_INFERENCE_THREADS", false, "3")]
    [InlineData("InferenceThreads", "FLEXYPARSE_NEMOTRON_INFERENCE_THREADS", true, "4")]
    public void StandardNemotronSettingHasPriorityOverAlias(string setting, string alias, bool commandLine, string expected)
    {
        using var configuration = new ConfigurationManager();
        configuration.Sources.Add(new EnvironmentVariablesConfigurationSource());
        // Model a standard environment setting without changing shared process variables.
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"FlexyParse:Recognition:Nemotron:{setting}"] = "3"
        });
        if (commandLine) configuration.AddCommandLine([$"--FlexyParse:Recognition:Nemotron:{setting}=4"]);
        HostConfiguration.Apply(configuration, variable => variable == alias ? "2" : null);
        Assert.Equal(expected, configuration[$"FlexyParse:Recognition:Nemotron:{setting}"]);
    }

    [Theory]
    [InlineData("DETECTION_MODEL_PATH", "DetectionModelPath")]
    [InlineData("CLASSIFICATION_MODEL_PATH", "ClassificationModelPath")]
    [InlineData("ESLAV_RECOGNITION_MODEL_PATH", "Profiles:eslav:RecognitionModelPath")]
    [InlineData("ESLAV_DICTIONARY_PATH", "Profiles:eslav:DictionaryPath")]
    [InlineData("LATIN_RECOGNITION_MODEL_PATH", "Profiles:latin:RecognitionModelPath")]
    [InlineData("LATIN_DICTIONARY_PATH", "Profiles:latin:DictionaryPath")]
    [InlineData("CYRILLIC_RECOGNITION_MODEL_PATH", "Profiles:cyrillic:RecognitionModelPath")]
    [InlineData("CYRILLIC_DICTIONARY_PATH", "Profiles:cyrillic:DictionaryPath")]
    public void RemovedModelPathAliasesDoNotOverrideStandardConfiguration(string aliasSuffix, string setting)
    {
        using var configuration = new ConfigurationManager();
        var key = "FlexyParse:Recognition:RapidOcr:" + setting;
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "configured-path" });
        HostConfiguration.Apply(configuration, name => name == "FLEXYPARSE_RAPIDOCR_" + aliasSuffix ? "obsolete-path" : null);
        Assert.Equal("configured-path", configuration[key]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShippedSettingsValidateWithoutLocalModels(bool enableRapidOcr)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json").Build();
        if (enableRapidOcr)
        {
            configuration["FlexyParse:Recognition:Provider"] = "RapidOcr";
        }
        var services = new ServiceCollection();
        services.AddConfiguredFlexyParse(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value;
        Assert.Equal(enableRapidOcr ? RecognitionProvider.RapidOcr : RecognitionProvider.OpenAi, options.Recognition.Provider);
        Assert.Equal("eslav", options.Recognition.Language);
        if (enableRapidOcr)
        {
            var local = provider.GetRequiredService<IOptions<RapidOcrOptions>>().Value;
            Assert.Empty(local.DetectionModelPath);
            Assert.Equal("models/rapidocr/v3.10.0", local.ModelsDirectory);
        }
        else
        {
            Assert.Null(provider.GetService<RapidOcrModelStore>());
        }
        Assert.Null(configuration["FlexyParse:Recognition:RapidOcr:Profiles:eslav:RecognitionModelPath"]);
    }
}
