using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlexyParse.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlexyParse.Tests;

public sealed partial class HttpConversionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnsupportedParametersAreIgnoredAndLoggedWithoutValues(bool sourceRequest, bool background)
    {
        using var logs = new ParameterLogProvider();
        await using var server = await TestServer.StartAsync(services => services.AddLogging(builder => builder.AddProvider(logs)));
        var suffix = background ? "/async" : "";
        HttpResponseMessage submitted;
        if (sourceRequest)
        {
            submitted = await server.Client.PostAsJsonAsync("/v1/convert/source" + suffix, new
            {
                file_sources = new[] { new { filename = "report.md", base64_string = Convert.ToBase64String("# Report"u8), extra_source = "source-secret" } },
                callbacks = new[] { "callback-secret" },
                options = new
                {
                    to_formats = new[] { "md" }, do_ocr = false,
                    table_mode = "table-secret", force_ocr = new { token = "nested-secret" }, ocr_lang = new[] { "lang-secret" }
                }
            });
        }
        else
        {
            using var form = Form("# Report", "report.md");
            form.Add(new StringContent("table-secret"), "table_mode");
            form.Add(new StringContent("{\"token\":\"nested-secret\"}"), "force_ocr");
            form.Add(new StringContent("lang-secret"), "ocr_lang");
            form.Add(new StringContent("callback-secret"), "callbacks");
            form.Add(new ByteArrayContent("source-secret"u8.ToArray()), "extra_source", "secret.txt");
            submitted = await server.Client.PostAsync("/v1/convert/file" + suffix, form);
        }

        using (submitted)
        {
            submitted.EnsureSuccessStatusCode();
            if (background)
            {
                var task = await submitted.Content.ReadFromJsonAsync<ConversionTaskStatus>();
                await WaitForTaskAsync(server.Client, task!.TaskId, "success");
                using var result = await server.Client.GetAsync($"/v1/result/{task.TaskId}");
                result.EnsureSuccessStatusCode();
                Assert.Equal("# Report", await ReadMarkdownResultAsync(result));
            }
            else
            {
                Assert.Equal("# Report", await ReadMarkdownResultAsync(submitted));
            }
        }

        var warnings = logs.Entries.ToArray();
        Assert.Equal(5, warnings.Length); // Source payload is parsed again in the worker: do not log twice.
        foreach (var name in new[] { "table_mode", "force_ocr", "ocr_lang", "callbacks", "extra_source" })
        {
            var warning = Assert.Single(warnings, entry => entry.Message.Contains(name, StringComparison.Ordinal));
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Contains("was ignored", warning.Message);
            Assert.NotNull(warning.ParameterName);
        }
        Assert.DoesNotContain(warnings, entry => entry.Message.Contains("-secret", StringComparison.Ordinal)
            || entry.ParameterName!.Contains("-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("table_mode", "fast")]
    [InlineData("format", "json")]
    [InlineData("recognizeImages", "false")]
    [InlineData("future_option", "null")]
    public async Task UnknownMultipartFieldsDoNotChangeSupportedDefaults(string name, string value)
    {
        await using var server = await TestServer.StartAsync();
        using var form = Form("# Report", "report.md");
        form.Add(new StringContent(value), name);
        using var response = await server.Client.PostAsync("/v1/convert/file", form);
        response.EnsureSuccessStatusCode();
        Assert.Equal("# Report", await ReadMarkdownResultAsync(response));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("[1,2]")]
    [InlineData("{\"token\":\"private-value\"}")]
    public async Task UnsupportedJsonValuesOfAnyShapeAreIgnored(string value)
    {
        using var logs = new ParameterLogProvider();
        await using var server = await TestServer.StartAsync(services => services.AddLogging(builder => builder.AddProvider(logs)));
        var json = "{\"file_sources\":[{\"filename\":\"a.md\",\"base64_string\":\"YQ==\"}],\"options\":{\"future_option\":" + value + "}}";
        using var response = await server.Client.PostAsync("/v1/convert/source", new StringContent(json, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        Assert.Equal("a", await ReadMarkdownResultAsync(response));
        Assert.Contains("options.future_option", Assert.Single(logs.Entries).Message);
        Assert.DoesNotContain("private-value", Assert.Single(logs.Entries).Message);
    }

    [Fact]
    public async Task UnsupportedJsonNamesMatchingInternalPropertiesAreAlsoLogged()
    {
        using var logs = new ParameterLogProvider();
        await using var server = await TestServer.StartAsync(services => services.AddLogging(builder => builder.AddProvider(logs)));
        using var response = await server.Client.PostAsJsonAsync("/v1/convert/source", new
        {
            file_sources = new[] { new { filename = "a.md", base64_string = "YQ==" } },
            options = new { formats = "ignored", export_mode = "ignored" }
        });
        response.EnsureSuccessStatusCode();
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("options.formats", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("options.export_mode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IgnoredNamesAreEscapedAndBoundedInLogs()
    {
        using var logs = new ParameterLogProvider();
        await using var server = await TestServer.StartAsync(services => services.AddLogging(builder => builder.AddProvider(logs)));
        var payload = new Dictionary<string, object?>
        {
            ["file_sources"] = new[] { new { filename = "a.md", base64_string = "YQ==" } },
            ["unknown\r\nforged-entry"] = "secret",
            [new string('x', 1000)] = "secret"
        };
        using var response = await server.Client.PostAsJsonAsync("/v1/convert/source", payload);
        response.EnsureSuccessStatusCode();
        Assert.Equal(2, logs.Entries.Count);
        foreach (var entry in logs.Entries)
        {
            Assert.DoesNotContain('\r', entry.Message);
            Assert.DoesNotContain('\n', entry.Message);
            Assert.DoesNotContain("secret", entry.Message);
            Assert.True(entry.ParameterName!.Length < 300);
        }
    }

    [Fact]
    public async Task SupportedParametersDoNotProduceIgnoredWarnings()
    {
        using var logs = new ParameterLogProvider();
        await using var server = await TestServer.StartAsync(services => services.AddLogging(builder => builder.AddProvider(logs)));
        using var form = Form("# Report", "report.md");
        using var response = await server.Client.PostAsync("/v1/convert/file", form);
        response.EnsureSuccessStatusCode();
        Assert.Empty(logs.Entries);
    }

    private static async Task<string?> ReadMarkdownResultAsync(HttpResponseMessage response)
    {
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return result.RootElement.GetProperty("document").GetProperty("md_content").GetString();
    }

    private sealed class ParameterLogProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message, string? ParameterName)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ParameterLogger(this, categoryName);
        public void Dispose() { }

        private sealed class ParameterLogger(ParameterLogProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category != "FlexyParse.Hosting.ConversionParameters")
                {
                    return;
                }
                var properties = state as IEnumerable<KeyValuePair<string, object?>>;
                var name = properties?.FirstOrDefault(property => property.Key == "ParameterName").Value as string;
                provider.Entries.Enqueue((logLevel, formatter(state, exception), name));
            }
        }
    }
}
