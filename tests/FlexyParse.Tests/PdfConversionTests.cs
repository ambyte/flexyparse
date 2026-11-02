using System.Text;
using System.Text.Json;
using FlexyParse.Extraction;
using FlexyParse.Markdown;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Output;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FlexyParse.Tests;

public class PdfConversionTests
{
    [Fact]
    public async Task ConvertsGeneratedPdfToJsonAndMarkdown()
    {
        var root = Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new FlexyParseOptions
            {
                RecognizeImages = false,
                MaxUploadBytes = 5_000_000,
                MaxConcurrentDocuments = 2
            });
            var service = new DocumentConversionService(
                options,
                new DocumentProcessingGate(options),
                new DocumentExtractor(
                    options,
                    new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance),
                    new PdfImageExtractor(NullLogger<PdfImageExtractor>.Instance, new PdfImageFallbackRenderer(options)),
                    new TabulaTableExtractor(NullLogger<TabulaTableExtractor>.Instance),
                    NullLogger<DocumentExtractor>.Instance),
                new RejectingRecognizer(),
                new MarkdownDocumentWriter(new JsonDocumentWriter(new MarkdownJsonParser()), new MarkdownJsonFormatter()),
                new JsonDocumentWriter(new MarkdownJsonParser()),
                new HtmlDocumentWriter(new MarkdownDocumentWriter(new JsonDocumentWriter(new MarkdownJsonParser()), new MarkdownJsonFormatter())),
                NullLogger<DocumentConversionService>.Instance,
                new GotenbergPdfConverter(new RejectingHttpClientFactory(), options));

            var pdf = BuildPdf();
            await using var json = new MemoryStream();
            await service.ConvertAsync(
                new ConversionInput
                {
                    Content = new MemoryStream(pdf),
                    FileName = "hello.pdf",
                    Format = OutputFormat.Json,
                    RecognizeImages = false
                },
                json,
                CancellationToken.None);

            var jsonText = Encoding.UTF8.GetString(json.ToArray());
            Assert.Contains("Hello FlexyParse", jsonText, StringComparison.Ordinal);
            Assert.Contains("Second line of the document", jsonText, StringComparison.Ordinal);

            await using var markdown = new MemoryStream();
            await service.ConvertAsync(
                new ConversionInput
                {
                    Content = new MemoryStream(pdf),
                    FileName = "hello.pdf",
                    Format = OutputFormat.Markdown,
                    RecognizeImages = false
                },
                markdown,
                CancellationToken.None);
            var markdownText = Encoding.UTF8.GetString(markdown.ToArray());
            Assert.Contains("Hello FlexyParse", markdownText, StringComparison.Ordinal);

            await using var html = new MemoryStream();
            await service.ConvertAsync(new ConversionInput
            {
                Content = new MemoryStream(pdf),
                FileName = "hello.pdf",
                Format = OutputFormat.Html,
                RecognizeImages = false
            }, html, CancellationToken.None);
            var htmlText = Encoding.UTF8.GetString(html.ToArray());
            Assert.StartsWith("<!DOCTYPE html>", htmlText, StringComparison.Ordinal);
            Assert.Contains("Hello FlexyParse", htmlText, StringComparison.Ordinal);
            Assert.Contains("<strong>Page 1</strong>", htmlText, StringComparison.Ordinal);
            Assert.True(html.CanWrite);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RejectsNonPdf()
    {
        var root = Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
        var options = Microsoft.Extensions.Options.Options.Create(new FlexyParseOptions { MaxUploadBytes = 1000 });
        var service = new DocumentConversionService(
            options,
            new DocumentProcessingGate(options),
            new DocumentExtractor(
                options,
                new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance),
                new PdfImageExtractor(NullLogger<PdfImageExtractor>.Instance, new PdfImageFallbackRenderer(options)),
                new TabulaTableExtractor(NullLogger<TabulaTableExtractor>.Instance),
                NullLogger<DocumentExtractor>.Instance),
            new RejectingRecognizer(),
            new MarkdownDocumentWriter(new JsonDocumentWriter(new MarkdownJsonParser()), new MarkdownJsonFormatter()),
            new JsonDocumentWriter(new MarkdownJsonParser()),
            new HtmlDocumentWriter(new MarkdownDocumentWriter(new JsonDocumentWriter(new MarkdownJsonParser()), new MarkdownJsonFormatter())),
            NullLogger<DocumentConversionService>.Instance,
            new GotenbergPdfConverter(new RejectingHttpClientFactory(), options));

        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => service.ConvertAsync(
            new ConversionInput
            {
                Content = new MemoryStream("not a pdf"u8.ToArray()),
                FileName = "note.txt",
                Format = OutputFormat.Json
            },
            new MemoryStream(),
            CancellationToken.None));

        Assert.Equal(ConversionStatus.BadRequest, exception.StatusCode);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LogsEveryPageAndTracksAllPagesForRepeatedImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
        var workspace = JobWorkspace.Create(root, "multipage.pdf");
        try
        {
            var logger = new RecordingLogger<DocumentExtractor>();
            var options = Microsoft.Extensions.Options.Options.Create(new FlexyParseOptions());
            var extractor = new DocumentExtractor(
                options,
                new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance),
                new PdfImageExtractor(NullLogger<PdfImageExtractor>.Instance, new PdfImageFallbackRenderer(options)),
                new TabulaTableExtractor(NullLogger<TabulaTableExtractor>.Instance),
                logger);

            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            // A generated 10 x 10 RGB PNG, reused twice on page 1 and once on page 3.
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAoAAAAKCAIAAAACUFjqAAAAE0lEQVR4nGP4z8CAB+GT+z9ypQG3ymOdJh6tQAAAAABJRU5ErkJggg==");
            for (var number = 1; number <= 3; number++)
            {
                var page = builder.AddPage(595, 842);
                page.AddText("Page " + number, 12, new PdfPoint(72, 760), font);
                if (number != 2)
                {
                    page.AddPng(png, new PdfRectangle(72, 500, 172, 600));
                    if (number == 1)
                    {
                        page.AddPng(png, new PdfRectangle(200, 500, 300, 600));
                    }
                }
            }

            await File.WriteAllBytesAsync(workspace.SourcePath, builder.Build());
            var index = await extractor.ExtractAsync(workspace, CancellationToken.None);

            var image = Assert.Single(index.Images);
            Assert.Equal([1, 3], image.PageNumbers);
            var progress = logger.Entries.Where(entry => entry.Message.StartsWith("Processing page", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, progress.Length);
            for (var number = 1; number <= 3; number++)
            {
                Assert.Equal(number, progress[number - 1].Properties["Page"]);
                Assert.Equal(3, progress[number - 1].Properties["PageCount"]);
                Assert.Equal("multipage.pdf", progress[number - 1].Properties["File"]);
                Assert.Equal(workspace.JobId, progress[number - 1].Properties["JobId"]);
            }

            var manifest = JsonSerializer.Deserialize<List<ImageManifestEntry>>(await File.ReadAllTextAsync(workspace.ManifestPath), JsonDefaults.Options);
            Assert.NotNull(manifest);
            Assert.Equal([1, 3], Assert.Single(manifest).PageNumbers);
        }
        finally
        {
            await workspace.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] BuildPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText("Hello FlexyParse", 18, new PdfPoint(72, 760), font);
        page.AddText("Second line of the document", 12, new PdfPoint(72, 730), font);
        return builder.Build();
    }

    private sealed class RejectingRecognizer : IImageRecognizer
    {
        public Task RecognizeAsync(JobWorkspace workspace, Model.DocumentIndex index, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Image recognition is not used in this test.");
        }
    }

    private sealed class RejectingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Gotenberg is not used in this test.");
    }
}
