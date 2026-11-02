using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlexyParse.Hosting;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace FlexyParse.Tests;

public sealed partial class HttpConversionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AllRapidOcrModelPrefixesAreAcceptedAndPropagated(bool source, bool background)
    {
        var converter = new LanguageRecordingConverter();
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(converter));
        foreach (var model in FlexyParse.Recognition.OcrLanguageProfiles.ModelNames)
        {
            converter.Inputs.Clear();
            using var response = await SubmitLanguageAsync(server.Client, source, background, " " + model.ToUpperInvariant() + " ");
            response.EnsureSuccessStatusCode();
            if (background)
            {
                var task = await response.Content.ReadFromJsonAsync<ConversionTaskStatus>();
                await WaitForTaskAsync(server.Client, task!.TaskId, "success");
            }
            Assert.Equal(2, converter.Inputs.Count);
            Assert.All(converter.Inputs, input => Assert.Equal(model, input.OcrLanguage));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OcrLanguageIsNormalizedAndPropagatedForEveryFormat(bool source, bool background)
    {
        var converter = new LanguageRecordingConverter();
        await using var server = await TestServer.StartAsync(services =>
        {
            services.AddSingleton<IDocumentConverter>(converter);
            services.Configure<FlexyParseOptions>(options => options.Recognition.Language = "en");
        });
        using var response = await SubmitLanguageAsync(server.Client, source, background, "eslav");
        response.EnsureSuccessStatusCode();
        if (background)
        {
            var task = await response.Content.ReadFromJsonAsync<ConversionTaskStatus>();
            await WaitForTaskAsync(server.Client, task!.TaskId, "success");
            using var result = await server.Client.GetAsync($"/v1/result/{task.TaskId}");
            result.EnsureSuccessStatusCode();
        }

        Assert.Equal(2, converter.Inputs.Count);
        Assert.All(converter.Inputs, input =>
        {
            Assert.Equal("eslav", input.OcrLanguage);
            Assert.False(input.RecognizeImages);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvalidOcrLanguageIsRejectedBeforeConversionOrQueueAcceptance(bool source, bool background)
    {
        var converter = new LanguageRecordingConverter();
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(converter));
        foreach (var language in new[] { "de", "", "  ", "ru,en", "not-a-language", "ru", "uk", "be", "ru-RU", "uk-UA", "be-BY", "en-US" })
        {
            using var response = await SubmitLanguageAsync(server.Client, source, background, language);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.False(problem.RootElement.TryGetProperty("task_id", out _));
        }
        Assert.Empty(converter.Inputs);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OmittedOcrLanguagePreservesCoreConfiguredDefault(bool source, bool background)
    {
        var converter = new LanguageRecordingConverter();
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(converter));
        using var response = await SubmitLanguageAsync(server.Client, source, background, null);
        response.EnsureSuccessStatusCode();
        if (background)
        {
            var task = await response.Content.ReadFromJsonAsync<ConversionTaskStatus>();
            await WaitForTaskAsync(server.Client, task!.TaskId, "success");
        }
        Assert.Equal(2, converter.Inputs.Count);
        Assert.All(converter.Inputs, input => Assert.Null(input.OcrLanguage));
    }

    [Fact]
    public async Task DuplicateMultipartOcrLanguageIsRejected()
    {
        await using var server = await TestServer.StartAsync();
        using var form = Form("# Test", "test.md");
        form.Add(new StringContent("eslav"), "ocr_language");
        form.Add(new StringContent("en"), "ocr_language");
        using var response = await server.Client.PostAsync("/v1/convert/file/async", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("[\"ru\"]")]
    public async Task NonStringJsonOcrLanguageIsRejectedBeforeAcceptance(string value)
    {
        await using var server = await TestServer.StartAsync();
        var json = "{\"file_sources\":[{\"filename\":\"test.md\",\"base64_string\":\"IyBUZXN0\"}],\"options\":{\"ocr_language\":" + value + "}}";
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await server.Client.PostAsync("/v1/convert/source/async", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OcrLanguageIsAdvertisedInCapabilitiesAndAllRequestSchemas()
    {
        await using var server = await TestServer.StartAsync();
        using var capabilities = await server.Client.GetAsync("/v1/capabilities");
        using var body = await JsonDocument.ParseAsync(await capabilities.Content.ReadAsStreamAsync());
        Assert.Contains(body.RootElement.GetProperty("options").EnumerateArray(), item => item.GetString() == "ocr_language");
        using var schemaResponse = await server.Client.GetAsync("/openapi.json");
        using var schema = await JsonDocument.ParseAsync(await schemaResponse.Content.ReadAsStreamAsync());
        foreach (var route in new[] { "/v1/convert/file", "/v1/convert/source", "/v1/convert/file/async", "/v1/convert/source/async" })
        {
            var contentType = route.Contains("/file", StringComparison.Ordinal) ? "multipart/form-data" : "application/json";
            var properties = schema.RootElement.GetProperty("paths").GetProperty(route).GetProperty("post")
                .GetProperty("requestBody").GetProperty("content").GetProperty(contentType).GetProperty("schema").GetProperty("properties");
            if (contentType == "application/json") { properties = properties.GetProperty("options").GetProperty("properties"); }
            var language = properties.GetProperty("ocr_language");
            Assert.Equal("string", language.GetProperty("type").GetString());
            Assert.Contains(FlexyParse.Recognition.OcrLanguageProfiles.ModelLanguagesDescription, language.GetProperty("description").GetString());
        }
    }

    private static async Task<HttpResponseMessage> SubmitLanguageAsync(HttpClient client, bool source, bool background, string? language)
    {
        var suffix = background ? "/async" : "";
        if (source)
        {
            var options = new Dictionary<string, object> { ["to_formats"] = new[] { "md", "html" }, ["do_ocr"] = false };
            if (language is not null) { options["ocr_language"] = language; }
            return await client.PostAsJsonAsync("/v1/convert/source" + suffix,
                new { file_sources = new[] { Base64Source("# Language", "language.md") }, options });
        }
        using var form = Form("# Language", "language.md");
        form.Add(new StringContent("md"), "to_formats");
        form.Add(new StringContent("html"), "to_formats");
        form.Add(new StringContent("false"), "do_ocr");
        if (language is not null) { form.Add(new StringContent(language), "ocr_language"); }
        return await client.PostAsync("/v1/convert/file" + suffix, form);
    }

    private sealed class LanguageRecordingConverter : IDocumentConverter
    {
        public ConcurrentQueue<ConversionInput> Inputs { get; } = new();

        public async Task ConvertAsync(ConversionInput input, Stream destination, CancellationToken cancellationToken)
        {
            Inputs.Enqueue(input);
            await destination.WriteAsync("Converted"u8.ToArray(), cancellationToken);
        }
    }
}
