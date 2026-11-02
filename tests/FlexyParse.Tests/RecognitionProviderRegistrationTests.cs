using FlexyParse.DependencyInjection;
using FlexyParse.Hosting;
using FlexyParse.Options;
using FlexyParse.RapidOcr;
using FlexyParse.Recognition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FlexyParse.Tests;

public sealed class RecognitionProviderRegistrationTests
{
    [Fact]
    public void CoreDoesNotReferenceRapidOcrAssembliesOrOptions()
    {
        var references = typeof(IImageRecognizer).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name is "RapidOcrNet" or "FlexyParse.Recognition.RapidOcr");
        Assert.Null(typeof(RecognitionOptions).GetProperty("RapidOcr"));
        Assert.NotEqual(typeof(IImageRecognizer).Assembly, typeof(RapidOcrImageRecognizer).Assembly);
    }

    [Fact]
    public async Task DefaultHostDoesNotInstallLocalProviderOrStartupService()
    {
        var services = Services();
        services.AddConfiguredFlexyParse(Configuration());
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<OpenAiImageRecognizer>(provider.GetRequiredService<IImageRecognizer>());
        Assert.Null(provider.GetService<RapidOcrModelStore>());
        Assert.Null(provider.GetService<IRapidOcrSessionFactory>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), service => service is RapidOcrStartupService);
    }

    [Fact]
    public async Task HostSelectsLocalProviderAndBindsExistingConfigurationSectionWithoutLoadingModels()
    {
        var configuration = Configuration(new()
        {
            ["FlexyParse:Recognition:Provider"] = "RapidOcr",
            ["FlexyParse:Recognition:Language"] = "eslav",
            ["FlexyParse:Recognition:RapidOcr:ModelsDirectory"] = "custom-ocr-cache",
            ["FlexyParse:Recognition:RapidOcr:MaxConcurrency"] = "2",
            ["FlexyParse:Recognition:RapidOcr:InferenceThreads"] = "3"
        });
        var services = Services();
        services.AddConfiguredFlexyParse(configuration);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.IsType<RapidOcrImageRecognizer>(provider.GetRequiredService<IImageRecognizer>());
        Assert.Single(provider.GetServices<IImageRecognizer>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<RapidOcrStartupService>());
        var local = provider.GetRequiredService<IOptions<RapidOcrOptions>>().Value;
        Assert.Equal("custom-ocr-cache", local.ModelsDirectory);
        Assert.Equal(2, local.MaxConcurrency);
        Assert.Equal(3, local.InferenceThreads);
        Assert.Equal("eslav", provider.GetRequiredService<IOptions<RecognitionOptions>>().Value.Language);
    }

    [Fact]
    public async Task CoreOnlyWithLocalProviderSelectedFailsInsteadOfUsingExternalFallback()
    {
        var services = Services();
        services.AddFlexyParse(Configuration(new() { ["FlexyParse:Recognition:Provider"] = "RapidOcr" }));
        await using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IImageRecognizer>());
        Assert.Contains("external fallback is disabled", exception.Message);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4")]
    public void HostRejectsUnknownProviderAtComposition(string value)
    {
        var services = Services();
        Assert.Throws<OptionsValidationException>(() => services.AddConfiguredFlexyParse(
            Configuration(new() { ["FlexyParse:Recognition:Provider"] = value })));
    }

    [Theory]
    [InlineData("Language", "de")]
    [InlineData("RapidOcr:MaxConcurrency", "0")]
    [InlineData("RapidOcr:Profiles:latin:DictionaryPath", "missing-dictionary.txt")]
    [InlineData("RapidOcr:Profiles:unknown:DictionaryPath", "")]
    public async Task HostValidatesModuleSettingsBeforeStartup(string setting, string value)
    {
        var services = Services();
        services.AddConfiguredFlexyParse(Configuration(new()
        {
            ["FlexyParse:Recognition:Provider"] = "RapidOcr",
            ["FlexyParse:Recognition:" + setting] = value
        }));
        await using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public async Task ModuleRegistrationIsExplicitAndDoesNotDuplicateRecognizerOrStartupService()
    {
        var configuration = Configuration(new() { ["FlexyParse:Recognition:Provider"] = "RapidOcr" });
        var services = Services();
        services.AddRapidOcrRecognition(configuration);
        services.AddFlexyParse(configuration);
        services.AddRapidOcrRecognition(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<RapidOcrImageRecognizer>(Assert.Single(provider.GetServices<IImageRecognizer>()));
        Assert.Single(provider.GetServices<IHostedService>().OfType<RapidOcrStartupService>());
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static IConfiguration Configuration(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();
}
