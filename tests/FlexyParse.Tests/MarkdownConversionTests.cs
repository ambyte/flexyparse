using System.Text;
using System.Text.Json;
using FlexyParse.DependencyInjection;
using FlexyParse.Hosting;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlexyParse.Tests;

public class MarkdownConversionTests
{
    [Fact]
    public async Task ConvertsMarkdownToHtmlThroughCanonicalJsonWithoutRecognition()
    {
        var root = NewRoot();
        try
        {
            using var services = CreateServices();
            const string markdown = "# Report\n\n- **Owner:** Ada\n\n| Name | Qty |\n| --- | --- |\n| A | 2 |\n\n<script>alert('unsafe')</script>";
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
            await using var destination = new MemoryStream();
            await services.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
            {
                Content = content,
                FileName = "report.md",
                Format = OutputFormat.Html,
                RecognizeImages = true
            }, destination, CancellationToken.None);

            var html = Encoding.UTF8.GetString(destination.ToArray());
            Assert.Contains("<h1>Report</h1>", html, StringComparison.Ordinal);
            Assert.Contains("<style>", html, StringComparison.Ordinal);
            Assert.Contains("<meta name=\"viewport\"", html, StringComparison.Ordinal);
            Assert.Contains("<strong>Owner:</strong> Ada", html, StringComparison.Ordinal);
            Assert.Contains("<table>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
            Assert.EndsWith("</body>\n</html>\n", html, StringComparison.Ordinal);
            Assert.True(content.CanRead);
            Assert.True(destination.CanWrite);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("report.md", false, false)]
    [InlineData("REPORT.MD", true, false)]
    [InlineData("report.markdown", true, true)]
    public async Task ConvertsMarkdownWithoutCallingRecognizer(string fileName, bool fromPath, bool moveSource)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var services = CreateServices();
            var sourcePath = Path.Combine(root, fileName);
            const string markdown = "# Report\n\n- **Owner:** Ada\n\n| Name | Qty |\n| --- | --- |\n| A | 2 |";
            await File.WriteAllTextAsync(sourcePath, markdown);
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
            await using var destination = new MemoryStream();

            await services.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
            {
                Content = fromPath ? null : content,
                SourcePath = fromPath ? sourcePath : null,
                MoveSource = moveSource,
                FileName = fileName,
                Format = OutputFormat.Json,
                RecognizeImages = true
            }, destination, CancellationToken.None);

            using var document = JsonDocument.Parse(destination.ToArray());
            Assert.Equal(fileName, document.RootElement.GetProperty("sourceFileName").GetString());
            Assert.False(document.RootElement.TryGetProperty("metadata", out _));
            var page = Assert.Single(document.RootElement.GetProperty("pages").EnumerateArray());
            Assert.Equal(1, page.GetProperty("number").GetInt32());
            Assert.False(page.TryGetProperty("width", out _));
            var blocks = page.GetProperty("blocks");
            Assert.Equal("heading", blocks[0].GetProperty("type").GetString());
            Assert.Equal("Report", blocks[0].GetProperty("text").GetString());
            Assert.Equal("list", blocks[1].GetProperty("type").GetString());
            Assert.Equal("table", blocks[2].GetProperty("type").GetString());
            Assert.Equal(!moveSource, File.Exists(sourcePath));
            Assert.Empty(Directory.EnumerateDirectories(root));
            Assert.True(content.CanRead);
            Assert.True(destination.CanWrite);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MarkdownOutputPreservesOriginalBytes()
    {
        var root = NewRoot();
        try
        {
            using var services = CreateServices();
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("# Title\r\n\r\nText  \r\nNext line\r\n")).ToArray();
            await using var content = new MemoryStream(bytes);
            await using var destination = new MemoryStream();
            await services.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
            {
                Content = content,
                FileName = "report.md",
                Format = OutputFormat.Markdown
            }, destination, CancellationToken.None);

            Assert.Equal(bytes, destination.ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOversizedMarkdownAndCleansWorkspace(bool fromPath)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var services = CreateServices(maxUploadBytes: 5);
            var path = Path.Combine(root, "large.md");
            await File.WriteAllTextAsync(path, "# Too large");
            await using var content = new MemoryStream("# Too large"u8.ToArray());
            await using var destination = new MemoryStream();
            var exception = await Assert.ThrowsAsync<DocumentConversionException>(() =>
                services.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
                {
                    Content = fromPath ? null : content,
                    SourcePath = fromPath ? path : null,
                    FileName = "large.md",
                    Format = OutputFormat.Json
                }, destination, CancellationToken.None));

            Assert.Equal(ConversionStatus.PayloadTooLarge, exception.StatusCode);
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliDoesNotOverwriteMarkdownSource(bool explicitOutput)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var services = CreateServices();
            var path = Path.Combine(root, "report.md");
            const string markdown = "# Must not be overwritten";
            await File.WriteAllTextAsync(path, markdown);
            var runner = new CliRunner(services.GetRequiredService<IDocumentConverter>(),
                services.GetRequiredService<IOptions<FlexyParseOptions>>(), NullLogger<CliRunner>.Instance);
            var args = new List<string> { "convert", path, "--format", "markdown" };
            if (explicitOutput)
            {
                args.AddRange(["--output", path]);
            }

            Assert.Equal(1, await runner.RunAsync(args.ToArray(), CancellationToken.None));
            Assert.Equal(markdown, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider CreateServices(long maxUploadBytes = 100_000)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:MaxUploadBytes"] = maxUploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(configuration);
        services.AddSingleton<IImageRecognizer>(new RejectingRecognizer());
        return services.BuildServiceProvider();
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));

    private sealed class RejectingRecognizer : IImageRecognizer
    {
        public Task RecognizeAsync(JobWorkspace workspace, DocumentIndex index, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Markdown input must not invoke image recognition.");
    }
}
