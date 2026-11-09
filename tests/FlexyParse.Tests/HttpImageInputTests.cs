using System.Net.Http.Json;
using System.Text.Json;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace FlexyParse.Tests;

public sealed partial class HttpConversionTests
{
    [Fact]
    public async Task CapabilitiesAdvertiseDirectRasterFormats()
    {
        await using var server = await TestServer.StartAsync();
        using var response = await server.Client.GetAsync("/v1/capabilities");
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var formats = result.RootElement.GetProperty("from_formats").EnumerateArray().Select(item => item.GetString()).ToArray();
        foreach (var format in new[] { "png", "jpg", "jpeg", "webp", "bmp", "gif", "ico" })
        {
            Assert.Contains(format, formats);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileAndBase64SourceImagesUseRecognitionWithoutPdfConversion(bool sourceRequest)
    {
        var recognizer = new HttpRasterRecognizer();
        await using var server = await TestServer.StartAsync(services =>
        {
            services.Configure<FlexyParseOptions>(options =>
            {
                options.ModelApi.RequireApiKey = false;
                options.GotenbergUrl = "http://gotenberg:3000";
            });
            services.AddSingleton<IImageRecognizer>(recognizer);
            services.AddHttpClient(GotenbergPdfConverter.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new RejectRasterConversionHandler());
        });
        using var bitmap = new SKBitmap(128, 64, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.White);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        HttpResponseMessage response;
        if (sourceRequest)
        {
            response = await server.Client.PostAsJsonAsync("/v1/convert/source", new
            {
                file_sources = new[] { new { filename = "scan.png", base64_string = Convert.ToBase64String(bytes) } },
                options = new { to_formats = new[] { "json" }, do_ocr = true }
            });
        }
        else
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(bytes), "files", "scan.png");
            form.Add(new StringContent("json"), "to_formats");
            form.Add(new StringContent("true"), "do_ocr");
            response = await server.Client.PostAsync("/v1/convert/file", form);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("success", result.RootElement.GetProperty("status").GetString());
            var pages = result.RootElement.GetProperty("document").GetProperty("json_content").GetProperty("pages");
            var page = Assert.Single(pages.EnumerateArray());
            var blocks = page.GetProperty("blocks").EnumerateArray().ToArray();
            Assert.Equal("image", blocks[0].GetProperty("type").GetString());
            Assert.Equal("Direct raster OCR", Assert.Single(blocks, block => block.GetProperty("type").GetString() == "paragraph").GetProperty("text").GetString());
            Assert.Equal(1, recognizer.Calls);
        }
    }

    private sealed class RejectRasterConversionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Raster uploads must not call Gotenberg.");
    }

    private sealed class HttpRasterRecognizer : IImageRecognizer
    {
        public int Calls { get; private set; }

        public async Task RecognizeAsync(JobWorkspace workspace, DocumentIndex index, CancellationToken cancellationToken)
        {
            Calls++;
            var image = Assert.Single(index.Images);
            Assert.Equal("image/png", image.MediaType);
            await File.WriteAllTextAsync(workspace.RecognitionPath(image.Hash),
                """[{"type":"paragraph","text":"Direct raster OCR"}]""", cancellationToken);
        }
    }
}
