using FlexyParse.Cli;
using FlexyParse.Pipeline;

namespace FlexyParse.Tests;

public class CliArgumentsTests
{
    [Theory]
    [InlineData("html")]
    [InlineData("HTML")]
    [InlineData(" html ")]
    public void ParsesHtmlAndUsesHtmlExtension(string format)
    {
        Assert.True(CliArguments.TryParse(["convert", "report.pdf", "-f", format, "-d", "out"], out var command, out var error));
        Assert.Null(error);
        Assert.Equal(OutputFormat.Html, command.Format);
        Assert.Equal(".html", CliArguments.Extension(command.Format));
        Assert.Equal(Path.Combine("out", "report.html"), CliArguments.ResolveOutputPath("report.pdf", command));
    }

    [Fact]
    public void ParsesConvertCommand()
    {
        var ok = CliArguments.TryParse(
            ["convert", "a.pdf", "b.pdf", "--format", "markdown", "--output-dir", "out", "--recognize-images", "false"],
            out var command,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliCommandKind.Convert, command.Kind);
        Assert.Equal(OutputFormat.Markdown, command.Format);
        Assert.Equal(["a.pdf", "b.pdf"], command.Files);
        Assert.False(command.RecognizeImages);
        Assert.Equal(Path.Combine("out", "a.md"), CliArguments.ResolveOutputPath("a.pdf", command));
    }

    [Fact]
    public void RejectsSingleOutputForSeveralFiles()
    {
        var ok = CliArguments.TryParse(["convert", "a.pdf", "b.pdf", "-o", "out.md"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("--output-dir", error, StringComparison.Ordinal);
    }
}
