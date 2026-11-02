using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlexyParse.DependencyInjection;
using FlexyParse.Hosting;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlexyParse.Tests;

public sealed partial class HttpConversionTests
{
    private const string TestApiKey = "test-host-key";

    [Theory]
    [InlineData("", "", "")]
    [InlineData("", "alias-key", "")]
    [InlineData("section-key", "alias-key", "section-key")]
    public void AuthenticationOnlyBindsItsSectionWithoutApplyingHostAliases(string section, string alias, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:Host:ApiKey"] = section,
            ["FLEXYPARSE_API_KEY"] = alias
        }).Build();
        var services = new ServiceCollection();
        ApiKeyAuthentication.AddServices(services, configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(expected, provider.GetRequiredService<IOptions<ApiKeyOptions>>().Value.ApiKey);
    }

    [Fact]
    public async Task EmptyApiKeyPreservesAnonymousAccessEvenWithArbitraryHeader()
    {
        await using var server = await TestServer.StartAsync();
        server.Client.DefaultRequestHeaders.Add("X-Api-Key", "not-configured");
        using var response = await server.Client.GetAsync("/v1/capabilities");
        response.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("/v1/capabilities", "GET", null)]
    [InlineData("/v1/capabilities", "GET", "")]
    [InlineData("/v1/capabilities", "GET", "wrong-key")]
    [InlineData("/v1/capabilities", "GET", "TEST-HOST-KEY")]
    [InlineData("/v1/convert/file", "POST", null)]
    [InlineData("/v1/convert/source", "POST", null)]
    [InlineData("/V1/capabilities", "GET", null)]
    public async Task ApiKeyRejectsUnauthorizedRequests(string path, string method, string? key)
    {
        await using var server = await TestServer.StartAsync(apiKey: TestApiKey);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        }

        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain(TestApiKey, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiKeyRejectsMultipleValues()
    {
        await using var server = await TestServer.StartAsync(apiKey: TestApiKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/capabilities");
        request.Headers.TryAddWithoutValidation("X-Api-Key", new[] { TestApiKey, TestApiKey });
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidApiKeyAllowsCapabilitiesAndBothConversionRoutes()
    {
        await using var server = await TestServer.StartAsync(apiKey: TestApiKey);
        server.Client.DefaultRequestHeaders.Add("x-api-key", TestApiKey);
        using var capabilities = await server.Client.GetAsync("/v1/capabilities");
        capabilities.EnsureSuccessStatusCode();
        using var form = Form("# Hello", "hello.md");
        using var fileResponse = await server.Client.PostAsync("/v1/convert/file", form);
        fileResponse.EnsureSuccessStatusCode();
        using var sourceResponse = await server.Client.PostAsJsonAsync("/v1/convert/source", new
        {
            file_sources = new[] { Base64Source("# Hello", "hello.md") },
            options = new { to_formats = new[] { "md" }, do_ocr = false }
        });
        sourceResponse.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/docs")]
    [InlineData("/openapi.json")]
    public async Task ApiKeyLeavesHealthUiAndDocumentationPublic(string path)
    {
        await using var server = await TestServer.StartAsync(apiKey: TestApiKey);
        using var response = await server.Client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain(TestApiKey, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(TestApiKey)]
    public async Task OpenApiSecurityMatchesConfiguredApiKey(string key)
    {
        await using var server = await TestServer.StartAsync(apiKey: key);
        using var response = await server.Client.GetAsync("/openapi.json");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            var operation = path.Value.EnumerateObject().Single().Value;
            var secured = key.Length > 0 && path.Name.StartsWith("/v1/", StringComparison.Ordinal);
            Assert.Equal(secured, operation.TryGetProperty("security", out var security) && security.GetArrayLength() > 0);
            Assert.Equal(secured, operation.GetProperty("responses").TryGetProperty("401", out _));
        }
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("key with spaces")]
    [InlineData("key\r\n")]
    [InlineData("key\u00e9")]
    public async Task InvalidConfiguredApiKeyFailsStartup(string key)
    {
        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(
            () => TestServer.StartAsync(apiKey: key));
    }

    [Fact]
    public async Task SwaggerUiAndAssetsAreAvailableInProduction()
    {
        await using var server = await TestServer.StartAsync();
        using var response = await server.Client.GetAsync("/docs");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Swagger UI", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        foreach (var asset in new[] { "/docs/swagger-ui.css", "/docs/swagger-ui-bundle.js", "/docs/index.js" })
        {
            using var assetResponse = await server.Client.GetAsync(asset);
            assetResponse.EnsureSuccessStatusCode();
            var content = await assetResponse.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(content));
            if (asset.EndsWith("index.js", StringComparison.Ordinal))
            {
                Assert.Contains("../openapi.json", content, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task OpenApiDescribesSupportedRoutesBodiesAndResponseTypes()
    {
        await using var server = await TestServer.StartAsync(services => services.Configure<FlexyParseOptions>(options =>
        {
            options.MaxFilesPerRequest = 2;
            options.MaxUploadBytes = 32;
        }));
        using var response = await server.Client.GetAsync("/openapi.json");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        Assert.StartsWith("3.", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.Equal("FlexyParse HTTP API", root.GetProperty("info").GetProperty("title").GetString());
        var paths = root.GetProperty("paths");
        Assert.Equal(["/health", "/v1/capabilities", "/v1/convert/file", "/v1/convert/file/async", "/v1/convert/source", "/v1/convert/source/async",
            "/v1/result/{task_id}", "/v1/status/poll/{task_id}", "/v1/status/ws/{task_id}"],
            paths.EnumerateObject().Select(path => path.Name).Order(StringComparer.Ordinal));

        var fileOperation = paths.GetProperty("/v1/convert/file").GetProperty("post");
        var multipart = fileOperation.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data");
        var fileSchema = multipart.GetProperty("schema");
        Assert.Contains(fileSchema.GetProperty("required").EnumerateArray(), value => value.GetString() == "files");
        Assert.False(fileSchema.GetProperty("additionalProperties").GetBoolean());
        var properties = fileSchema.GetProperty("properties");
        Assert.Equal("array", properties.GetProperty("files").GetProperty("type").GetString());
        Assert.Equal("binary", properties.GetProperty("files").GetProperty("items").GetProperty("format").GetString());
        Assert.Equal(2, properties.GetProperty("files").GetProperty("maxItems").GetInt32());
        Assert.Contains("32 bytes", properties.GetProperty("files").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("md", properties.GetProperty("to_formats").GetProperty("default")[0].GetString());
        Assert.Equal(["md", "json", "html"], properties.GetProperty("to_formats").GetProperty("items").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.True(properties.GetProperty("do_ocr").GetProperty("default").GetBoolean());
        Assert.True(multipart.GetProperty("encoding").GetProperty("to_formats").GetProperty("explode").GetBoolean());

        var sourceOperation = paths.GetProperty("/v1/convert/source").GetProperty("post");
        var sourceProperties = sourceOperation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("properties");
        Assert.Equal("uri", sourceProperties.GetProperty("http_sources").GetProperty("items").GetProperty("properties").GetProperty("url").GetProperty("format").GetString());
        var base64 = sourceProperties.GetProperty("file_sources").GetProperty("items");
        Assert.Contains(base64.GetProperty("required").EnumerateArray(), value => value.GetString() == "filename");
        Assert.Contains(base64.GetProperty("required").EnumerateArray(), value => value.GetString() == "base64_string");
        Assert.Equal("byte", base64.GetProperty("properties").GetProperty("base64_string").GetProperty("format").GetString());
        Assert.False(sourceProperties.GetProperty("options").GetProperty("additionalProperties").GetBoolean());

        var responses = fileOperation.GetProperty("responses");
        var success = responses.GetProperty("200").GetProperty("content");
        Assert.True(success.TryGetProperty("application/zip", out _));
        var envelope = success.GetProperty("application/json").GetProperty("schema").GetProperty("properties");
        Assert.Equal("success", envelope.GetProperty("status").GetProperty("enum")[0].GetString());
        Assert.True(envelope.GetProperty("document").GetProperty("properties").TryGetProperty("json_content", out _));
        foreach (var code in new[] { "400", "413", "499", "503" })
        {
            Assert.True(responses.GetProperty(code).GetProperty("content").TryGetProperty("application/problem+json", out _));
        }
        Assert.True(sourceOperation.GetProperty("responses").TryGetProperty("504", out _));
    }

    [Fact]
    public async Task SwaggerSourceExampleCanBeConverted()
    {
        await using var server = await TestServer.StartAsync();
        using var schemaResponse = await server.Client.GetAsync("/openapi.json");
        schemaResponse.EnsureSuccessStatusCode();
        using var schema = await JsonDocument.ParseAsync(await schemaResponse.Content.ReadAsStreamAsync());
        var example = schema.RootElement.GetProperty("paths").GetProperty("/v1/convert/source").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example");
        using var content = new StringContent(example.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await server.Client.PostAsync("/v1/convert/source", content);
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("# Hello", result.RootElement.GetProperty("document").GetProperty("md_content").GetString());
    }

    [Fact]
    public async Task FileEndpointDefaultsToMarkdownAndReturnsDoclingEnvelope()
    {
        await using var server = await TestServer.StartAsync();
        using var form = Form("# Report", "report.md");
        using var response = await server.Client.PostAsync("/v1/convert/file", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = result.RootElement;
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("# Report", root.GetProperty("document").GetProperty("md_content").GetString());
        Assert.False(root.GetProperty("document").TryGetProperty("json_content", out _));
        Assert.True(root.GetProperty("processing_time").GetDouble() >= 0);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("timings").ValueKind);
        Assert.Empty(root.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task SupportsMultipleOutputFormatsForOneDocument()
    {
        await using var server = await TestServer.StartAsync();
        using var form = Form("# Report\n\n**Owner:** Ada", "report.md");
        foreach (var format in new[] { "md", "json", "html" })
        {
            form.Add(new StringContent(format), "to_formats");
        }

        form.Add(new StringContent("false"), "do_ocr");
        using var response = await server.Client.PostAsync("/v1/convert/file", form);
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var document = result.RootElement.GetProperty("document");
        Assert.Contains("# Report", document.GetProperty("md_content").GetString(), StringComparison.Ordinal);
        Assert.Contains("<h1>Report</h1>", document.GetProperty("html_content").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, document.GetProperty("json_content").ValueKind);
        Assert.Equal(JsonValueKind.Array, document.GetProperty("json_content").GetProperty("pages").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleDocumentsReturnZipWithUniqueNames(bool sourceRequest)
    {
        await using var server = await TestServer.StartAsync();
        HttpResponseMessage response;
        if (sourceRequest)
        {
            response = await server.Client.PostAsJsonAsync("/v1/convert/source", new
            {
                file_sources = new[] { Base64Source("# First", "report.md"), Base64Source("# Second", "report.md") },
                options = new { to_formats = new[] { "html", "md" }, do_ocr = false }
            });
        }
        else
        {
            using var form = Form("# First", "report.md");
            form.Add(new StringContent("# Second"), "files", "report.md");
            form.Add(new StringContent("html"), "to_formats");
            form.Add(new StringContent("md"), "to_formats");
            response = await server.Client.PostAsync("/v1/convert/file", form);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("flexyparse.zip", response.Content.Headers.ContentDisposition?.FileNameStar);
            using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
            Assert.Equal(4, archive.Entries.Count);
            Assert.Equal(["report-2.html", "report-2.md", "report.html", "report.md"],
                archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
            foreach (var entry in archive.Entries.Where(entry => entry.Name.EndsWith(".html", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(entry.Open());
                Assert.StartsWith("<!DOCTYPE html>", await reader.ReadToEndAsync(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Base64SourceUsesFilenameAndOptions()
    {
        var converter = new RecordingConverter();
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(converter));
        using var response = await server.Client.PostAsJsonAsync("/v1/convert/source", new
        {
            file_sources = new[] { Base64Source("# Hello", "../report.md") },
            options = new { to_formats = new[] { "html" }, do_ocr = false }
        });
        response.EnsureSuccessStatusCode();
        Assert.Equal("report.md", converter.FileName);
        Assert.Equal("# Hello", converter.Content);
        Assert.False(converter.DoOcr);
        Assert.Equal(OutputFormat.Html, converter.Format);
        Assert.False(Directory.Exists(converter.UploadDirectory));
    }

    [Fact]
    public async Task Base64DecodesAcrossChunkBoundariesAndWhitespace()
    {
        var content = "# Report\n" + new string('x', 50_000);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content), Base64FormattingOptions.InsertLineBreaks);
        await using var server = await TestServer.StartAsync();
        using var response = await server.Client.PostAsJsonAsync("/v1/convert/source", new
        {
            file_sources = new[] { new { filename = "large.md", base64_string = base64 + "\n " } }
        });
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(content, result.RootElement.GetProperty("document").GetProperty("md_content").GetString());
    }

    [Fact]
    public async Task HttpSourceStreamsThroughNamedClientAndConverts()
    {
        var handler = new SourceHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("# Downloaded", Encoding.UTF8)
        });
        await using var server = await TestServer.StartAsync(services => services.AddHttpClient(SourceDocumentService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler));
        using var response = await server.Client.PostAsJsonAsync("/v1/convert/source", new
        {
            http_sources = new[] { new { url = "https://example.com/report.md?signature=secret" } }
        });
        response.EnsureSuccessStatusCode();
        Assert.Equal("https://example.com/report.md?signature=secret", handler.Uri?.AbsoluteUri);
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("# Downloaded", result.RootElement.GetProperty("document").GetProperty("md_content").GetString());
    }

    [Theory]
    [InlineData("file:///c:/secret.md")]
    [InlineData("http://localhost/secret.md")]
    [InlineData("http://127.0.0.1/secret.md")]
    [InlineData("http://[::1]/secret.md")]
    [InlineData("http://169.254.169.254/secret.md")]
    [InlineData("http://10.0.0.1/secret.md")]
    [InlineData("http://172.16.0.1/secret.md")]
    [InlineData("http://192.168.0.1/secret.md")]
    [InlineData("http://[::ffff:192.168.0.1]/secret.md")]
    [InlineData("http://[fd00::1]/secret.md")]
    [InlineData("https://user:password@example.com/report.md")]
    [InlineData("https://example.com/report.md#fragment")]
    public async Task RejectsUnsafeSourceUrls(string url)
    {
        await using var server = await TestServer.StartAsync();
        using var response = await server.Client.PostAsJsonAsync("/v1/convert/source", new { http_sources = new[] { new { url } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("password", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpSourceErrorsAndRedirectsAreRejected(HttpStatusCode status)
    {
        var handler = new SourceHandler(() => new HttpResponseMessage(status));
        await using var server = await TestServer.StartAsync(services => services.AddHttpClient(SourceDocumentService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler));
        using var response = await server.Client.PostAsJsonAsync("/v1/convert/source", new
        {
            http_sources = new[] { new { url = "https://example.com/report.md" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{invalid")]
    [InlineData("{\"http_sources\":null}")]
    [InlineData("{\"file_sources\":[null]}")]
    [InlineData("{\"file_sources\":[{}]}")]
    [InlineData("{\"file_sources\":[{\"filename\":\"report.md\",\"base64_string\":\"not-base64\"}]}")]
    [InlineData("{\"file_sources\":[{\"filename\":\"report.md\",\"base64_string\":\"YQ==Yg==\"}]}")]
    [InlineData("{\"file_sources\":[{\"filename\":\"report.md\",\"base64_string\":\"YQ==\"}],\"options\":null}")]
    [InlineData("{\"file_sources\":[{\"filename\":\"report.md\",\"base64_string\":\"YQ==\"}],\"options\":{\"to_formats\":[]}}")]
    [InlineData("{\"file_sources\":[{\"filename\":\"report.md\",\"base64_string\":\"YQ==\"}],\"options\":{\"to_formats\":null}}")]
    [InlineData("{\"file_sources\":[{\"filename\":\"report.md\",\"base64_string\":\"YQ==\"}],\"options\":{\"to_formats\":[\"text\"]}}")]
    [InlineData("{\"file_sources\":[{\"filename\":\"report.md\",\"base64_string\":\"YQ==\"}],\"options\":{\"table_mode\":\"fast\"}}")]
    [InlineData("{\"file_sources\":[{\"filename\":\"report.md\",\"base64_string\":\"YQ==\"}],\"options\":{\"do_ocr\":\"false\"}}")]
    public async Task RejectsMalformedOrUnsupportedSourceRequests(string json)
    {
        await using var server = await TestServer.StartAsync();
        using var response = await server.Client.PostAsync("/v1/convert/source", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("format", "json")]
    [InlineData("recognizeImages", "false")]
    [InlineData("to_formats", "text")]
    [InlineData("to_formats", "markdown")]
    [InlineData("do_ocr", "yes")]
    [InlineData("table_mode", "fast")]
    [InlineData("callbacks", "https://example.com/callback")]
    public async Task RejectsUnsupportedFileOptions(string name, string value)
    {
        await using var server = await TestServer.StartAsync();
        using var form = Form("# Report", "report.md");
        form.Add(new StringContent(value), name);
        using var response = await server.Client.PostAsync("/v1/convert/file", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnforcesSizeAndCountLimitsOnAllSourceKinds(bool knownLength)
    {
        var handler = new SourceHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength
                ? new ByteArrayContent(new byte[9])
                : new StreamContent(new NonSeekableMemoryStream(new byte[9]))
        });
        await using var server = await TestServer.StartAsync(services =>
        {
            services.Configure<FlexyParseOptions>(options => { options.MaxUploadBytes = 8; options.MaxFilesPerRequest = 1; });
            services.AddHttpClient(SourceDocumentService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        using var form = Form(new string('x', 9), "large.md");
        using var fileResponse = await server.Client.PostAsync("/v1/convert/file", form);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, fileResponse.StatusCode);
        using var base64Response = await server.Client.PostAsJsonAsync("/v1/convert/source", new { file_sources = new[] { Base64Source(new string('x', 9), "large.md") } });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, base64Response.StatusCode);
        using var urlResponse = await server.Client.PostAsJsonAsync("/v1/convert/source", new { http_sources = new[] { new { url = "https://example.com/large.md" } } });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, urlResponse.StatusCode);
        using var multiple = Form("a", "first.md");
        multiple.Add(new StringContent("b"), "files", "second.md");
        using var countResponse = await server.Client.PostAsync("/v1/convert/file", multiple);
        Assert.Equal(HttpStatusCode.BadRequest, countResponse.StatusCode);
        using var mixedResponse = await server.Client.PostAsJsonAsync("/v1/convert/source", new
        {
            file_sources = new[] { Base64Source("a", "first.md") },
            http_sources = new[] { new { url = "https://example.com/second.md" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, mixedResponse.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadBodyObservesTimeoutAndDisconnect(bool disconnect)
    {
        var stream = new BlockingSourceStream();
        var handler = new SourceHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        await using var server = await TestServer.StartAsync(services =>
        {
            services.Configure<SourceDownloadOptions>(options => options.TimeoutSeconds = disconnect ? 30 : 1);
            services.AddHttpClient(SourceDocumentService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = server.Client.PostAsJsonAsync("/v1/convert/source", new
        {
            http_sources = new[] { new { url = "https://example.com/report.md" } }
        }, cancellation.Token);
        await stream.Entered.Task.WaitAsync(cancellation.Token);
        if (disconnect)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        }
        else
        {
            using var response = await request;
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        }

        await stream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RejectsOversizedJsonBodyBeforeDeserialization()
    {
        await using var server = await TestServer.StartAsync(services =>
            services.Configure<FlexyParseOptions>(options => { options.MaxUploadBytes = 8; options.MaxFilesPerRequest = 1; }));
        using var body = new StringContent(new string('x', 1024 * 1024 + 32), Encoding.UTF8, "application/json");
        using var response = await server.Client.PostAsync("/v1/convert/source", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task MixedSourcesProduceOneArchive()
    {
        var handler = new SourceHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# HTTP") });
        await using var server = await TestServer.StartAsync(services => services.AddHttpClient(SourceDocumentService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler));
        using var response = await server.Client.PostAsJsonAsync("/v1/convert/source", new
        {
            http_sources = new[] { new { url = "https://example.com/remote.md" } },
            file_sources = new[] { Base64Source("# Base64", "local.md") }
        });
        response.EnsureSuccessStatusCode();
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.Equal(["local.md", "remote.md"], archive.Entries.Select(entry => entry.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedConversionCleansUploadsAndDoesNotReturnPartialArchive(bool multiple)
    {
        var converter = new FailingConverter();
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(converter));
        using var form = Form("# First", "first.md");
        if (multiple)
        {
            form.Add(new StringContent("# Second"), "files", "second.md");
        }

        using var response = await server.Client.PostAsync("/v1/convert/file", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(converter.UploadDirectories);
        Assert.All(converter.UploadDirectories, directory => Assert.False(Directory.Exists(directory)));
    }

    [Fact]
    public async Task ReportsCapabilitiesAndDoesNotExposeRemovedOrUnimplementedRoutes()
    {
        await using var server = await TestServer.StartAsync();
        using var response = await server.Client.GetAsync("/v1/capabilities");
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(result.RootElement.GetProperty("async_support").GetBoolean());
        Assert.Equal(["md", "json", "html"], result.RootElement.GetProperty("to_formats").EnumerateArray().Select(item => item.GetString()));
        foreach (var path in new[] { "/v1/convert", "/v1/convert/batch" })
        {
            using var missing = await server.Client.PostAsync(path, new StringContent(""));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using var cancellation = await server.Client.DeleteAsync($"/v1/convert/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, cancellation.StatusCode);
        using var health = await server.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var page = await server.Client.GetAsync("/");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("hx-post=\"v1/convert/file\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"files\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"to_formats\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"do_ocr\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"source-mode\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"source-url\" type=\"url\"", html, StringComparison.Ordinal);
        Assert.Contains("hx-ext=\"docling-source\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectCancelsConversionAndCleansUploads(bool multiple)
    {
        var converter = new BlockingConverter(multiple ? 2 : 1);
        await using var server = await TestServer.StartAsync(services => services.AddSingleton<IDocumentConverter>(converter));
        using var form = Form("# First", "first.md");
        if (multiple)
        {
            form.Add(new StringContent("# Second"), "files", "second.md");
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = server.Client.PostAsync("/v1/convert/file", form, cancellation.Token);
        await converter.Started.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        await converter.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForCleanupAsync(converter.UploadDirectory!);
    }

    [Fact]
    public async Task DisconnectReachesRealModelRequest()
    {
        var handler = new ConversionCancellationTests.BlockingModelHandler();
        await using var server = await TestServer.StartAsync(services =>
        {
            services.Configure<FlexyParseOptions>(options => { options.RecognizeImages = true; options.ModelApi.RequireApiKey = false; });
            services.AddHttpClient(OpenAiImageRecognizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(ConversionCancellationTests.BuildPdf(withImage: true)), "files", "image.pdf");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = server.Client.PostAsync("/v1/convert/file", form, cancellation.Token);
        await handler.Entered.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!handler.Cancelled)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static MultipartFormDataContent Form(string content, string name)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(content, Encoding.UTF8), "files", name);
        return form;
    }

    private static object Base64Source(string content, string filename) => new { filename, base64_string = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) };

    private static async Task WaitForCleanupAsync(string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Directory.Exists(directory))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class TestServer(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public Task StopAsync() => app.StopAsync();

        public static async Task<TestServer> StartAsync(Action<IServiceCollection>? configure = null, string apiKey = "")
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                EnvironmentName = "Production"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["FlexyParse:Host:ApiKey"] = apiKey;
            builder.Configuration["FLEXYPARSE_API_KEY"] = "";
            builder.Services.AddFlexyParse(builder.Configuration);
            SourceDocumentService.AddServices(builder.Services);
            ConversionTaskService.AddServices(builder.Services);
            ApiKeyAuthentication.AddServices(builder.Services, builder.Configuration);
            ApiDocumentation.AddServices(builder.Services);
            builder.Services.Configure<FlexyParseOptions>(options => options.RecognizeImages = false);
            configure?.Invoke(builder.Services);
            var app = builder.Build();
            app.UseWebSockets();
            ApiKeyAuthentication.Use(app);
            ApiDocumentation.Use(app);
            app.UseDefaultFiles();
            app.UseStaticFiles();
            ConvertEndpoints.Map(app);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return new TestServer(app, new HttpClient { BaseAddress = new Uri(Assert.Single(addresses!.Addresses)) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RecordingConverter : IDocumentConverter
    {
        public string? FileName { get; private set; }
        public string? Content { get; private set; }
        public bool? DoOcr { get; private set; }
        public OutputFormat Format { get; private set; }
        public string? UploadDirectory { get; private set; }

        public async Task ConvertAsync(ConversionInput input, Stream destination, CancellationToken cancellationToken)
        {
            FileName = input.FileName;
            Content = await File.ReadAllTextAsync(input.SourcePath!, cancellationToken);
            DoOcr = input.RecognizeImages;
            Format = input.Format;
            UploadDirectory = Path.GetDirectoryName(input.SourcePath);
            await destination.WriteAsync("<html>Converted</html>"u8.ToArray(), cancellationToken);
        }
    }

    private sealed class BlockingConverter(int expectedCount) : IDocumentConverter
    {
        private int _started;
        private int _cancelled;
        public string? UploadDirectory { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ConvertAsync(ConversionInput input, Stream destination, CancellationToken cancellationToken)
        {
            UploadDirectory = Path.GetDirectoryName(input.SourcePath);
            if (Interlocked.Increment(ref _started) == expectedCount)
            {
                Started.TrySetResult();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (Interlocked.Increment(ref _cancelled) == expectedCount)
                {
                    Cancelled.TrySetResult();
                }
                throw;
            }
        }
    }

    private sealed class SourceHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            return Task.FromResult(response());
        }
    }

    private sealed class NonSeekableMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class BlockingSourceStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingConverter : IDocumentConverter
    {
        public System.Collections.Concurrent.ConcurrentBag<string> UploadDirectories { get; } = [];

        public Task ConvertAsync(ConversionInput input, Stream destination, CancellationToken cancellationToken)
        {
            UploadDirectories.Add(Path.GetDirectoryName(input.SourcePath)!);
            throw new DocumentConversionException(ConversionStatus.BadRequest, "Test conversion failure.");
        }
    }
}
