using FlexyParse.Cli;
using FlexyParse.Hosting;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlexyParse.Tests;

public sealed class CliOcrLanguageTests
{
    [Fact]
    public void AllModelPrefixesAreAcceptedAndDocumented()
    {
        foreach (var model in FlexyParse.Recognition.OcrLanguageProfiles.ModelNames)
        {
            Assert.True(CliArguments.TryParse(["convert", "input.pdf", "--ocr-language", model.ToUpperInvariant()], out var command, out var error), error);
            Assert.Equal(model, command.OcrLanguage);
            Assert.Contains(model, CliArguments.Help);
        }
        Assert.Contains(FlexyParse.Recognition.OcrLanguageProfiles.ModelLanguagesDescription, CliArguments.Help);
    }

    [Theory]
    [InlineData("eslav", "eslav")]
    [InlineData(" ESLAV ", "eslav")]
    [InlineData("en", "en")]
    [InlineData("LATIN", "latin")]
    [InlineData("cyrillic", "cyrillic")]
    public void ParsesAndNormalizesLanguage(string language, string expected)
    {
        Assert.True(CliArguments.TryParse(["convert", "test.pdf", "--ocr-language", language], out var command, out var error));
        Assert.Null(error);
        Assert.Equal(expected, command.OcrLanguage);
        Assert.Contains("--ocr-language", CliArguments.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("ru")]
    [InlineData("uk")]
    [InlineData("be")]
    [InlineData("ru-RU")]
    [InlineData("uk-UA")]
    [InlineData("be-BY")]
    [InlineData("en-US")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ru,en")]
    [InlineData("--format")]
    public void RejectsUnsupportedLanguage(string language)
    {
        Assert.False(CliArguments.TryParse(["convert", "test.pdf", "--ocr-language", language], out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void MissingLanguageFailsAndOmissionRemainsUnset()
    {
        Assert.False(CliArguments.TryParse(["convert", "test.pdf", "--ocr-language"], out _, out _));
        Assert.True(CliArguments.TryParse(["convert", "test.pdf"], out var command, out _));
        Assert.Null(command.OcrLanguage);
    }

    [Theory]
    [InlineData("en", "en", 0)]
    [InlineData(null, null, 0)]
    [InlineData("de", null, 2)]
    public async Task RunnerPropagatesLanguageOrRejectsBeforeConversion(string? language, string? expected, int exitCode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flexyparse-cli-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "test.md");
            var output = Path.Combine(directory, "out.html");
            await File.WriteAllTextAsync(source, "# Test");
            var converter = new CliLanguageConverter();
            var runner = new CliRunner(converter, Microsoft.Extensions.Options.Options.Create(new FlexyParseOptions()), NullLogger<CliRunner>.Instance);
            var args = new List<string> { "convert", source, "--format", "html", "--output", output };
            if (language is not null) { args.AddRange(["--ocr-language", language]); }
            Assert.Equal(exitCode, await runner.RunAsync(args.ToArray(), CancellationToken.None));
            if (exitCode == 0)
            {
                Assert.NotNull(converter.Input);
                Assert.Equal(expected, converter.Input.OcrLanguage);
            }
            else
            {
                Assert.Null(converter.Input);
                Assert.False(File.Exists(output));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CliLanguageConverter : IDocumentConverter
    {
        public ConversionInput? Input { get; private set; }

        public Task ConvertAsync(ConversionInput input, Stream destination, CancellationToken cancellationToken)
        {
            Input = input;
            return Task.CompletedTask;
        }
    }
}
