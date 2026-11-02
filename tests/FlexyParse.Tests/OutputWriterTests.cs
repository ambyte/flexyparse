using System.Text;
using System.Text.Json;
using FlexyParse.Markdown;
using FlexyParse.Model;
using FlexyParse.Output;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using TagBites.Text.Markdown;

namespace FlexyParse.Tests;

public class OutputWriterTests
{
    [Fact]
    public async Task HtmlRendersJsonDerivedMarkdownAndRecognitionAcrossPages()
    {
        var root = NewRoot();
        var workspace = JobWorkspace.Create(root, "sample.pdf");
        try
        {
            await WritePageAsync(workspace);
            await WriteRecognitionAsync(workspace, RecognitionMarkdown());
            await WritePageAsync(workspace, number: 2);
            var index = new DocumentIndex
            {
                SourceFileName = "sample.pdf",
                Metadata = new DocumentMetadata { PageCount = 2, Title = "Report <script>alert(1)</script>" },
                Images = []
            };
            await using var destination = new MemoryStream();
            await new HtmlDocumentWriter(CreateMarkdownWriter()).WriteAsync(workspace, index, destination, CancellationToken.None);
            var html = Encoding.UTF8.GetString(destination.ToArray());

            Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
            Assert.Contains("<meta charset=\"utf-8\">", html, StringComparison.Ordinal);
            Assert.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">", html, StringComparison.Ordinal);
            Assert.Contains("<style>", html, StringComparison.Ordinal);
            Assert.Contains("@media (max-width: 640px)", html, StringComparison.Ordinal);
            Assert.Contains("@media print", html, StringComparison.Ordinal);
            Assert.Equal(1, CountOf(html, "<style>"));
            Assert.Contains("<title>Report &lt;script&gt;alert(1)&lt;/script&gt;</title>", html, StringComparison.Ordinal);
            Assert.Contains("<h1>Report</h1>", html, StringComparison.Ordinal);
            Assert.Contains("<table>", html, StringComparison.Ordinal);
            Assert.Contains("<th>Name</th>", html, StringComparison.Ordinal);
            Assert.Contains("<h3>Plan</h3>", html, StringComparison.Ordinal);
            Assert.Contains("<strong>Owner:</strong> Ada", html, StringComparison.Ordinal);
            Assert.Contains("<code class=\"language-text\">", html, StringComparison.Ordinal);
            Assert.Contains("After image.", html, StringComparison.Ordinal);
            Assert.Contains("<hr", html, StringComparison.Ordinal);
            Assert.Contains("<strong>Page 2</strong>", html, StringComparison.Ordinal);
            Assert.Equal(2, CountOf(html, "<h1>Report</h1>"));
            Assert.Equal(1, CountOf(html, "<!DOCTYPE html>"));
            Assert.DoesNotContain("source:", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
            Assert.True(destination.CanWrite);
        }
        finally
        {
            await workspace.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MarkdownIncludesTableAndImageRecognition()
    {
        var root = NewRoot();
        var workspace = JobWorkspace.Create(root, "sample.pdf");
        try
        {
            await WritePageAsync(workspace);
            await WriteRecognitionAsync(workspace, RecognitionMarkdown());

            await using var destination = new MemoryStream();
            await CreateMarkdownWriter().WriteAsync(workspace, Index(), destination, CancellationToken.None);
            var markdown = Encoding.UTF8.GetString(destination.ToArray());

            Assert.Contains("# Report", markdown, StringComparison.Ordinal);
            Assert.Contains("| Name | Value |", markdown, StringComparison.Ordinal);
            Assert.Contains("### Plan", markdown, StringComparison.Ordinal);
            Assert.Contains("A scheme.", markdown, StringComparison.Ordinal);
            Assert.Contains("Start here and continue", markdown, StringComparison.Ordinal);
            Assert.Contains("| Col | Qty |", markdown, StringComparison.Ordinal);
            Assert.Contains("| A", markdown, StringComparison.Ordinal);
            Assert.Contains("**Owner:** Ada", markdown, StringComparison.Ordinal);
            Assert.Contains("Formula: $E = mc^2$.", markdown, StringComparison.Ordinal);
            Assert.Contains("```text", markdown, StringComparison.Ordinal);
            Assert.Contains("Original text", markdown, StringComparison.Ordinal);
            Assert.True(markdown.IndexOf("| Name | Value |", StringComparison.Ordinal) < markdown.IndexOf("### Plan", StringComparison.Ordinal));
            Assert.True(markdown.IndexOf("### Plan", StringComparison.Ordinal) < markdown.IndexOf("After image.", StringComparison.Ordinal));
            Assert.DoesNotContain("visibleText", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("Image p1-i1", markdown, StringComparison.Ordinal);
            Assert.Equal(1, CountOf(markdown, "Plan"));
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

    [Fact]
    public async Task JsonInlinesRecognizedContentWithoutImageWrapper()
    {
        var root = NewRoot();
        var workspace = JobWorkspace.Create(root, "sample.pdf");
        try
        {
            await WritePageAsync(workspace);
            await WriteRecognitionAsync(workspace, RecognitionMarkdown());

            await using var destination = new MemoryStream();
            await new JsonDocumentWriter(new MarkdownJsonParser()).WriteAsync(workspace, Index(), destination, CancellationToken.None);
            using var document = JsonDocument.Parse(destination.ToArray());
            var blocks = document.RootElement.GetProperty("pages")[0].GetProperty("blocks");
            Assert.Equal("sample.pdf", document.RootElement.GetProperty("sourceFileName").GetString());
            Assert.Equal("heading", blocks[2].GetProperty("type").GetString());
            Assert.Equal(3, blocks[2].GetProperty("level").GetInt32());
            Assert.Equal("Plan", blocks[2].GetProperty("text").GetString());
            Assert.Equal("A scheme.", blocks[3].GetProperty("text").GetString());
            Assert.Equal("Start here and continue", blocks[4].GetProperty("text").GetString());
            Assert.Equal("table", blocks[5].GetProperty("type").GetString());
            Assert.Equal("list", blocks[6].GetProperty("type").GetString());
            Assert.All(blocks.EnumerateArray(), block =>
            {
                Assert.NotEqual("image", block.GetProperty("type").GetString());
                Assert.False(block.TryGetProperty("recognition", out _));
                Assert.False(block.TryGetProperty("contentHash", out _));
            });
            Assert.Equal(new[] { "number", "blocks" }, document.RootElement.GetProperty("pages")[0]
                .EnumerateObject().Select(property => property.Name));
            Assert.Equal(new[] { "title" }, document.RootElement.GetProperty("metadata")
                .EnumerateObject().Select(property => property.Name));
            Assert.Equal(new[] { "type", "level", "text" }, blocks[0].EnumerateObject().Select(property => property.Name));
            Assert.Equal(new[] { "type", "rows" }, blocks[1].EnumerateObject().Select(property => property.Name));
            Assert.Equal(10, blocks.GetArrayLength());
            Assert.Equal("After image.", blocks[9].GetProperty("text").GetString());
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

    [Theory]
    [InlineData(false, null, "Image p1-i1")]
    [InlineData(true, null, "The recognition result could not be interpreted.")]
    [InlineData(false, "Recognition unavailable", "Recognition unavailable")]
    public async Task JsonOmitsUnrecognizedImagesAndWritesErrorsAsContent(bool emptyRecognition, string? error, string expected)
    {
        var root = NewRoot();
        var workspace = JobWorkspace.Create(root, "sample.pdf");
        try
        {
            await WritePageAsync(workspace);
            if (emptyRecognition)
            {
                await File.WriteAllTextAsync(workspace.RecognitionPath("abc"), " \n ");
            }

            if (error is not null)
            {
                await File.WriteAllTextAsync(workspace.RecognitionErrorPath("abc"), error);
            }

            await using var markdownDestination = new MemoryStream();
            await CreateMarkdownWriter().WriteAsync(workspace, Index(), markdownDestination, CancellationToken.None);
            var markdown = Encoding.UTF8.GetString(markdownDestination.ToArray());
            if (emptyRecognition || error is not null)
            {
                Assert.Contains(expected, markdown, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain(expected, markdown, StringComparison.Ordinal);
            }
            Assert.Contains("After image.", markdown, StringComparison.Ordinal);

            await using var jsonDestination = new MemoryStream();
            await new JsonDocumentWriter(new MarkdownJsonParser()).WriteAsync(workspace, Index(), jsonDestination, CancellationToken.None);
            using var document = JsonDocument.Parse(jsonDestination.ToArray());
            var blocks = document.RootElement.GetProperty("pages")[0].GetProperty("blocks");
            if (emptyRecognition || error is not null)
            {
                Assert.Equal(4, blocks.GetArrayLength());
                Assert.Equal("error", blocks[2].GetProperty("type").GetString());
                Assert.Equal(expected, blocks[2].GetProperty("message").GetString());
            }
            else
            {
                Assert.Equal(3, blocks.GetArrayLength());
            }

            Assert.Equal("After image.", blocks[blocks.GetArrayLength() - 1].GetProperty("text").GetString());
            Assert.DoesNotContain("p1-i1", document.RootElement.GetRawText(), StringComparison.Ordinal);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task JsonPreservesNotesErrorsAndRecognizedContentInOrder(bool recognized, bool failed)
    {
        var root = NewRoot();
        var workspace = JobWorkspace.Create(root, "sample.pdf");
        try
        {
            const string note = "*Original note*";
            await WritePageAsync(workspace, note);
            if (recognized)
            {
                await WriteRecognitionAsync(workspace, "# Recognized");
            }

            if (failed)
            {
                await File.WriteAllTextAsync(workspace.RecognitionErrorPath("abc"), "Recognition warning");
            }

            await using var destination = new MemoryStream();
            await new JsonDocumentWriter(new MarkdownJsonParser()).WriteAsync(workspace, Index(), destination, CancellationToken.None);
            using var document = JsonDocument.Parse(destination.ToArray());
            var blocks = document.RootElement.GetProperty("pages")[0].GetProperty("blocks");
            Assert.Equal(4 + (recognized ? 1 : 0) + (failed ? 1 : 0), blocks.GetArrayLength());
            Assert.Equal("paragraph", blocks[2].GetProperty("type").GetString());
            Assert.Equal(((MarkdownText)note).Markdown, blocks[2].GetProperty("text").GetString());
            var index = 3;
            if (failed)
            {
                Assert.Equal("error", blocks[index].GetProperty("type").GetString());
                Assert.Equal("Recognition warning", blocks[index++].GetProperty("message").GetString());
            }

            if (recognized)
            {
                Assert.Equal("heading", blocks[index].GetProperty("type").GetString());
                Assert.Equal("Recognized", blocks[index++].GetProperty("text").GetString());
            }

            Assert.Equal("After image.", blocks[index].GetProperty("text").GetString());
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

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("not JSON")]
    public async Task BothOutputsReportInvalidRecognitionJsonWithoutDroppingFollowingContent(string content)
    {
        var root = NewRoot();
        var workspace = JobWorkspace.Create(root, "sample.pdf");
        try
        {
            await WritePageAsync(workspace);
            await File.WriteAllTextAsync(workspace.RecognitionPath("abc"), content);
            await using var jsonOutput = new MemoryStream();
            await new JsonDocumentWriter(new MarkdownJsonParser()).WriteAsync(workspace, Index(), jsonOutput, CancellationToken.None);
            using var json = JsonDocument.Parse(jsonOutput.ToArray());
            var blocks = json.RootElement.GetProperty("pages")[0].GetProperty("blocks");
            Assert.Equal("error", blocks[2].GetProperty("type").GetString());
            Assert.Equal("The recognition result could not be interpreted.", blocks[2].GetProperty("message").GetString());
            Assert.Equal("After image.", blocks[3].GetProperty("text").GetString());

            await using var markdownOutput = new MemoryStream();
            await CreateMarkdownWriter().WriteAsync(workspace, Index(), markdownOutput, CancellationToken.None);
            var markdown = Encoding.UTF8.GetString(markdownOutput.ToArray());
            Assert.Contains("The recognition result could not be interpreted.", markdown, StringComparison.Ordinal);
            Assert.Contains("After image.", markdown, StringComparison.Ordinal);
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

    private static MarkdownDocumentWriter CreateMarkdownWriter() =>
        new(new JsonDocumentWriter(new MarkdownJsonParser()), new MarkdownJsonFormatter());

    private static async Task WriteRecognitionAsync(JobWorkspace workspace, string markdown)
    {
        await using var stream = File.Create(workspace.RecognitionPath("abc"));
        await using var writer = new Utf8JsonWriter(stream);
        new MarkdownJsonParser().Write(writer, markdown);
        await writer.FlushAsync();
    }

    private static string RecognitionMarkdown()
    {
        return """
            ### Plan

            A scheme.

            Start here and continue

            | Col | Qty |
            | --- | --- |
            | A   | 2   |

            - **Owner:** Ada

            Formula: $E = mc^2$.

            ```text
            Original text
            ```
            """;
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string NewRoot()
    {
        return Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
    }

    private static DocumentIndex Index()
    {
        return new DocumentIndex
        {
            SourceFileName = "sample.pdf",
            Metadata = new DocumentMetadata
            {
                Title = "Report",
                Subject = "Internal subject",
                Producer = "PDF producer",
                CreationDate = "2026-01-01",
                PageCount = 1
            },
            Images = []
        };
    }

    private static async Task WritePageAsync(JobWorkspace workspace, string? note = null, int number = 1)
    {
        var page = new StoredPage
        {
            Number = number,
            Width = 595,
            Height = 842,
            Blocks =
            [
                new HeadingBlock { Level = 1, Text = "Report", Top = 800, Left = 40 },
                new TableBlock
                {
                    Top = 700,
                    Left = 40,
                    Rows = [new[] { "Name", "Value" }, new[] { "A", "1" }]
                },
                new ImageBlock
                {
                    Id = "p1-i1",
                    ContentHash = "abc",
                    PixelWidth = 10,
                    PixelHeight = 10,
                    Note = note,
                    Top = 500,
                    Left = 40
                },
                new ParagraphBlock { Text = "After image.", Top = 400, Left = 40 }
            ]
        };
        await using var stream = File.Create(workspace.PagePath(number));
        await JsonSerializer.SerializeAsync(stream, page, JsonDefaults.Options);
    }
}
