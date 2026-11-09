using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlexyParse.Hosting;
using SkiaSharp;

namespace FlexyParse.Tests;

public sealed partial class HttpConversionTests
{
    [Theory]
    [InlineData("placeholder", "json", false)]
    [InlineData("embedded", "json", false)]
    [InlineData("referenced", "json", false)]
    [InlineData("placeholder", "md", false)]
    [InlineData("embedded", "md", false)]
    [InlineData("referenced", "md", false)]
    [InlineData("embedded", "html", false)]
    [InlineData("referenced", "html", false)]
    [InlineData("placeholder", "json", true)]
    [InlineData("embedded", "json", true)]
    [InlineData("referenced", "json", true)]
    [InlineData("embedded", "md", true)]
    [InlineData("referenced", "md", true)]
    public async Task ImageExportModesWorkWithoutRecognition(string mode, string format, bool sourceRequest)
    {
        await using var server = await TestServer.StartAsync();
        var bytes = ExportPng();
        using var response = await SubmitImageExportAsync(server.Client, bytes, mode, format, sourceRequest);
        response.EnsureSuccessStatusCode();
        string content;
        if (mode == "referenced")
        {
            Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
            using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
            var image = Assert.Single(zip.Entries, entry => entry.FullName.StartsWith("images/", StringComparison.Ordinal));
            using var imageStream = image.Open();
            using var saved = new MemoryStream();
            await imageStream.CopyToAsync(saved);
            AssertExportedPng(saved.ToArray());
            var document = Assert.Single(zip.Entries, entry => !entry.FullName.StartsWith("images/", StringComparison.Ordinal));
            using var reader = new StreamReader(document.Open());
            content = await reader.ReadToEndAsync();
            Assert.Contains(image.FullName, content);
        }
        else
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var document = json.RootElement.GetProperty("document");
            content = format == "json" ? document.GetProperty("json_content").GetRawText()
                : document.GetProperty(format + "_content").GetString()!;
            if (format == "json" && mode == "embedded")
            {
                content = document.GetProperty("json_content").GetProperty("pages")[0].GetProperty("blocks")[0].GetProperty("src").GetString()!;
            }
        }

        if (mode == "embedded")
        {
            Assert.Contains("data:image/png;base64,", content);
            var data = System.Text.RegularExpressions.Regex.Match(content, "data:image/png;base64,([A-Za-z0-9+/=]+)");
            Assert.True(data.Success);
            AssertExportedPng(Convert.FromBase64String(data.Groups[1].Value));
        }
        else if (mode == "placeholder")
        {
            Assert.DoesNotContain("base64", content);
            Assert.Contains(format == "json" ? "\"type\":\"image\"" : "<!-- image -->", content);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncludeImagesFalseSuppressesExportsAndReferencedZip(bool sourceRequest)
    {
        await using var server = await TestServer.StartAsync();
        using var response = await SubmitImageExportAsync(server.Client, ExportPng(), "referenced", "json", sourceRequest, include: false);
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var blocks = result.RootElement.GetProperty("document").GetProperty("json_content").GetProperty("pages")[0].GetProperty("blocks");
        Assert.Empty(blocks.EnumerateArray());
    }

    [Theory]
    [InlineData("EMBEDDED", false)]
    [InlineData("bogus", false)]
    [InlineData("", false)]
    [InlineData("bogus", true)]
    public async Task InvalidImageExportModeIsRejected(string mode, bool sourceRequest)
    {
        await using var server = await TestServer.StartAsync();
        using var response = await SubmitImageExportAsync(server.Client, ExportPng(), mode, "md", sourceRequest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AsyncReferencedImagesRemainAvailableInResultArchive()
    {
        await using var server = await TestServer.StartAsync();
        using var submitted = await SubmitImageExportAsync(server.Client, ExportPng(), "referenced", "md", false, async: true);
        submitted.EnsureSuccessStatusCode();
        var task = await submitted.Content.ReadFromJsonAsync<ConversionTaskStatus>();
        await WaitForTaskAsync(server.Client, task!.TaskId, "success");
        using var response = await server.Client.GetAsync($"/v1/result/{task.TaskId}");
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Single(zip.Entries, entry => entry.FullName.StartsWith("images/", StringComparison.Ordinal));
        Assert.Single(zip.Entries, entry => entry.FullName.EndsWith(".md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeCapabilitiesAndSchemaAdvertiseImageExportOptions()
    {
        await using var server = await TestServer.StartAsync();
        using var capabilities = JsonDocument.Parse(await server.Client.GetStringAsync("/v1/capabilities"));
        var options = capabilities.RootElement.GetProperty("options").EnumerateArray().Select(value => value.GetString());
        Assert.Contains("image_export_mode", options);
        Assert.Contains("include_images", options);
        using var schema = JsonDocument.Parse(await server.Client.GetStringAsync("/openapi.json"));
        var properties = schema.RootElement.GetProperty("paths").GetProperty("/v1/convert/file").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema").GetProperty("properties");
        Assert.Equal(3, properties.GetProperty("image_export_mode").GetProperty("enum").GetArrayLength());
        Assert.True(properties.GetProperty("include_images").GetProperty("default").GetBoolean());
    }

    private static byte[] ExportPng()
    {
        // Deliberately too small for recognition: filtering must not remove export assets.
        using var bitmap = new SKBitmap(32, 32);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void AssertExportedPng(byte[] bytes)
    {
        // Raster input is normalized by ImageDocumentExtractor; compare decoded content, not PNG encoding.
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(32, bitmap.Width);
        Assert.Equal(32, bitmap.Height);
        Assert.Equal(SKColors.CornflowerBlue, bitmap.GetPixel(16, 16));
    }

    private static Task<HttpResponseMessage> SubmitImageExportAsync(HttpClient client, byte[] bytes, string mode,
        string format, bool sourceRequest, bool include = true, bool async = false)
    {
        var suffix = async ? "/async" : "";
        if (sourceRequest)
        {
            return client.PostAsJsonAsync("/v1/convert/source" + suffix, new
            {
                file_sources = new[] { new { filename = "scan.png", base64_string = Convert.ToBase64String(bytes) } },
                options = new { to_formats = new[] { format }, do_ocr = false, image_export_mode = mode, include_images = include }
            });
        }
        return PostFormAsync();

        async Task<HttpResponseMessage> PostFormAsync()
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(bytes), "files", "scan.png");
            form.Add(new StringContent(format), "to_formats");
            form.Add(new StringContent("false"), "do_ocr");
            form.Add(new StringContent(mode), "image_export_mode");
            form.Add(new StringContent(include ? "true" : "false"), "include_images");
            return await client.PostAsync("/v1/convert/file" + suffix, form);
        }
    }
}
