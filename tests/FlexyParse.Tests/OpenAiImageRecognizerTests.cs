using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FlexyParse.Markdown;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Output;
using FlexyParse.Recognition;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlexyParse.Tests;

public class OpenAiImageRecognizerTests
{
    [Theory]
    [InlineData("https://openrouter.ai/api/v1", true)]
    [InlineData("https://openrouter.ai/api/v1/", true)]
    [InlineData("https://OPENROUTER.AI:443/api/v1/", true)]
    [InlineData("https://api.openai.com/v1/", false)]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("http://openrouter.ai/api/v1/", false)]
    [InlineData("https://openrouter.ai:8443/api/v1/", false)]
    [InlineData("https://openrouter.ai/api/v2/", false)]
    [InlineData("https://openrouter.ai/api/v1/other/", false)]
    [InlineData("https://openrouter.ai.example.com/api/v1/", false)]
    public async Task SendsProviderExtensionsOnlyToTheOfficialEndpoint(string baseUrl, bool extensionsExpected)
    {
        var handler = new StubHandler();

        await RecognizeOnceAsync(handler, 1, async workspace =>
        {
            using var body = JsonDocument.Parse(handler.Body!);
            Assert.Equal(extensionsExpected, body.RootElement.TryGetProperty("reasoning", out _));
            Assert.Equal(extensionsExpected ? "FlexyParse" : null, handler.ApplicationTitle);
            Assert.Equal(extensionsExpected ? "https://example.com" : null, handler.Referer);
            Assert.Equal(new Uri(baseUrl.TrimEnd('/') + "/chat/completions"), handler.RequestUri);
            Assert.Equal("Bearer", handler.AuthorizationScheme);
            await AssertRecognitionAsync(workspace, "### Plan\n\nA box.\n\nStart");
        }, configure: options =>
        {
            options.BaseUrl = baseUrl;
            options.HttpReferer = "https://example.com";
        });
    }

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("max_completion_tokens")]
    public async Task SupportsConfiguredTokenLimitWithoutOptionalTemperature(string parameter)
    {
        var handler = new StubHandler();

        await RecognizeOnceAsync(handler, 1, workspace =>
        {
            using var body = JsonDocument.Parse(handler.Body!);
            Assert.Equal(4096, body.RootElement.GetProperty(parameter).GetInt32());
            Assert.False(body.RootElement.TryGetProperty(parameter == "max_tokens" ? "max_completion_tokens" : "max_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty("temperature", out _));
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            return Task.CompletedTask;
        }, configure: options =>
        {
            options.BaseUrl = "https://api.openai.com/v1/";
            options.TokenLimitParameter = parameter;
            options.Temperature = null;
        });
    }

    [Fact]
    public async Task SupportsLocalServerWithoutAuthorization()
    {
        var handler = new StubHandler();

        await RecognizeOnceAsync(handler, 1, workspace =>
        {
            Assert.Null(handler.AuthorizationScheme);
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            return Task.CompletedTask;
        }, configure: options =>
        {
            options.BaseUrl = "http://localhost:8000/v1/";
            options.ApiKey = "";
            options.RequireApiKey = false;
        });
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1/?query=1")]
    [InlineData("https://openrouter.ai/api/v1/#fragment")]
    [InlineData("https://user@openrouter.ai/api/v1/")]
    [InlineData("file:///api/v1/")]
    public void RejectsInvalidBaseAddresses(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => new ModelApiOptions { BaseUrl = baseUrl }.ResolveBaseAddress());
    }

    [Fact]
    public async Task SendsImageAndStoresCompactJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
        var workspace = JobWorkspace.Create(root, "sample.pdf");
        try
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            var relative = Path.Combine("images", "abc.png");
            await File.WriteAllBytesAsync(Path.Combine(workspace.Root, relative), payload);
            var handler = new StubHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
            var options = Microsoft.Extensions.Options.Options.Create(new FlexyParseOptions
            {
                ModelApi = new ModelApiOptions
                {
                    ApiKey = "test-key",
                    Model = "google/gemini-2.5-flash",
                    Temperature = 0,
                    MaxAttempts = 1
                }
            });
            var recognizer = new OpenAiImageRecognizer(new FixedClientFactory(client), options, NullLogger<OpenAiImageRecognizer>.Instance, new MarkdownJsonParser());
            var index = new DocumentIndex
            {
                SourceFileName = "sample.pdf",
                Metadata = new DocumentMetadata { PageCount = 1 },
                Images =
                [
                    new ImageManifestEntry
                    {
                        Hash = "abc",
                        RelativePath = relative,
                        MediaType = "image/png",
                        Width = 2,
                        Height = 2
                    }
                ]
            };

            await recognizer.RecognizeAsync(workspace, index, CancellationToken.None);

            Assert.NotNull(handler.Body);
            Assert.Contains("google/gemini-2.5-flash", handler.Body, StringComparison.Ordinal);
            Assert.Contains("\"effort\":\"none\"", handler.Body, StringComparison.Ordinal);
            Assert.Contains("data:image/png;base64,AQIDBA==", handler.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("response_format", handler.Body, StringComparison.Ordinal);
            Assert.Contains("Return only Markdown", handler.Body, StringComparison.Ordinal);
            Assert.Contains("at most four characters", handler.Body, StringComparison.Ordinal);
            Assert.Equal("Bearer", handler.AuthorizationScheme);
            using var requestBody = JsonDocument.Parse(handler.Body);
            Assert.Equal(0, requestBody.RootElement.GetProperty("temperature").GetDouble());
            Assert.EndsWith(".recognition.json", workspace.RecognitionPath("abc"), StringComparison.Ordinal);
            await AssertRecognitionAsync(workspace, "### Plan\n\nA box.\n\nStart");
            Assert.False(File.Exists(workspace.RecognitionPath("abc") + ".tmp"));
            Assert.Empty(Directory.EnumerateFiles(workspace.ImagesDirectory, "*.recognition.md"));
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
    public async Task StoresFormUnderlinesWithoutAnotherAttempt()
    {
        var answer = "Surname " + new string('_', 24);
        var handler = new ScriptedHandler(Chat(System.Text.Json.JsonSerializer.Serialize(answer)));

        await RecognizeOnceAsync(handler, 2, async workspace =>
        {
            Assert.Equal(1, handler.Calls);
            await AssertRecognitionAsync(workspace, answer);
            using var recognized = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("abc")));
            var text = recognized.RootElement[0].GetProperty("text").GetString();
            Assert.Contains("Surname", text, StringComparison.Ordinal);
            Assert.Contains(new string(MarkdownDelimiterSanitizer.Line, 24), text, StringComparison.Ordinal);
            Assert.DoesNotContain("____", text, StringComparison.Ordinal);
            Assert.False(File.Exists(workspace.RecognitionErrorPath("abc")));
        });
    }

    [Fact]
    public async Task RetriesWhenMarkdownNestingExceedsTheLimit()
    {
        var nested = string.Concat(Enumerable.Repeat("> ", 200)) + "visible";
        var handler = new ScriptedHandler(
            Chat(System.Text.Json.JsonSerializer.Serialize(nested)),
            Chat(System.Text.Json.JsonSerializer.Serialize("### Plan\n\nStart")));

        await RecognizeOnceAsync(handler, 2, async workspace =>
        {
            Assert.Equal(2, handler.Calls);
            await AssertRecognitionAsync(workspace, "### Plan\n\nStart");
            Assert.False(File.Exists(workspace.RecognitionErrorPath("abc")));
        });
    }

    [Fact]
    public async Task KeepsTheLastAttemptWhenNestingStillExceedsTheLimit()
    {
        var nested = "# Title\n\n" + string.Concat(Enumerable.Repeat("> ", 200)) + "visible";
        var handler = new ScriptedHandler(Chat(System.Text.Json.JsonSerializer.Serialize(nested)));

        await RecognizeOnceAsync(handler, 1, async workspace =>
        {
            Assert.Equal(1, handler.Calls);
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            Assert.False(File.Exists(workspace.RecognitionErrorPath("abc")));
            using var recognized = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("abc")));
            var combined = string.Join('\n', recognized.RootElement.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
            Assert.Contains("Title", combined, StringComparison.Ordinal);
            Assert.Contains("visible", combined, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("```markdown\n# Plan\n\nStart\n```", "# Plan\n\nStart")]
    [InlineData("```md\r\n# Plan\r\n```", "# Plan")]
    [InlineData("```\n# Plan\n```", "# Plan")]
    [InlineData("\uFEFF  # Plan  ", "# Plan")]
    [InlineData("```csharp\nvar x = 1;\n```", "```csharp\nvar x = 1;\n```")]
    [InlineData("# Plan\n\n```text\nStart\n```", "# Plan\n\n```text\nStart\n```")]
    public void NormalizesMarkdownWithoutChangingItsContent(string text, string expected)
    {
        Assert.Equal(expected, OpenAiImageRecognizer.NormalizeMarkdown(text));
    }

    [Theory]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null,\"reasoning\":\"Private draft\",\"reasoning_content\":\"Another draft\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":[{\"type\":\"reasoning\",\"text\":\"Private draft\"}]}}]}")]
    public async Task DoesNotStoreReasoningAsRecognition(string response)
    {
        var handler = new ScriptedHandler(response);

        await RecognizeOnceAsync(handler, 1, async workspace =>
        {
            Assert.False(File.Exists(workspace.RecognitionPath("abc")));
            Assert.Contains("empty response", await File.ReadAllTextAsync(workspace.RecognitionErrorPath("abc")), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SkipsReasoningPartAndStoresJson()
    {
        var content = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new Dictionary<string, string>
            {
                ["type"] = "reasoning",
                ["text"] = "Draft {\"example\":1}"
            },
            new Dictionary<string, string>
            {
                ["type"] = "text",
                ["text"] = "### Plan\n\nA box.\n\nStart"
            }
        });
        var handler = new ScriptedHandler(Chat(content));

        await RecognizeOnceAsync(handler, 1, async workspace =>
        {
            await AssertRecognitionAsync(workspace, "### Plan\n\nA box.\n\nStart");
        });
    }

    [Theory]
    [InlineData("\"provider response\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"choices\":[\"invalid choice\"]}")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"message\":\"invalid message\"}]}")]
    [InlineData("{\"choices\":[{\"message\":null}]}")]
    public async Task RetriesUnexpectedResponseTypesWithoutStoringParserExceptions(string response)
    {
        var handler = new ScriptedHandler(response, Chat(JsonSerializer.Serialize("Recognized text")));

        await RecognizeOnceAsync(handler, 2, async workspace =>
        {
            Assert.Equal(2, handler.Calls);
            await AssertRecognitionAsync(workspace, "Recognized text");
            Assert.False(File.Exists(workspace.RecognitionErrorPath("abc")));
        });
    }

    [Theory]
    [InlineData("\"provider unavailable\"", "provider unavailable")]
    [InlineData("{\"error\":\"provider unavailable\"}", "provider unavailable")]
    [InlineData("{\"error\":{\"message\":\"provider unavailable\"}}", "provider unavailable")]
    [InlineData("{\"error\":null}", "No error message.")]
    [InlineData("{\"error\":42}", "No error message.")]
    [InlineData("[]", "No error message.")]
    public async Task ReportsApiStatusForUnexpectedErrorTypes(string response, string expectedLogMessage)
    {
        var logger = new RecordingLogger<OpenAiImageRecognizer>();
        var handler = new CompletionTokenHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        });

        await RecognizeOnceAsync(handler, 1, async workspace =>
        {
            Assert.False(File.Exists(workspace.RecognitionPath("abc")));
            Assert.Equal("Model API returned status 400.", await File.ReadAllTextAsync(workspace.RecognitionErrorPath("abc")));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains(expectedLogMessage, StringComparison.Ordinal));
        }, logger);
    }

    [Fact]
    public async Task RetriesTransientApiFailureWithStringError()
    {
        var handler = new CompletionTokenHandler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":\"provider unavailable\"}", Encoding.UTF8, "application/json")
            }
            : RecognitionResponse());

        await RecognizeOnceAsync(handler, 2, async workspace =>
        {
            Assert.Equal(2, handler.TokenLimits.Count);
            await AssertRecognitionAsync(workspace, "### Plan\n\nA box.\n\nStart");
            Assert.False(File.Exists(workspace.RecognitionErrorPath("abc")));
        });
    }

    [Fact]
    public async Task RetriesWhenTheFirstAnswerIsEmpty()
    {
        var handler = new ScriptedHandler(
            Chat(System.Text.Json.JsonSerializer.Serialize("  ")),
            Chat(System.Text.Json.JsonSerializer.Serialize("### Plan\n\nStart")));

        await RecognizeOnceAsync(handler, 2, workspace =>
        {
            Assert.Equal(2, handler.Calls);
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            return Task.CompletedTask;
        });
    }

    [Fact(Timeout = 15000)]
    public async Task RetriesWhenProviderTimesOut()
    {
        var handler = new TimeoutHandler(1);

        await RecognizeOnceAsync(handler, 2, workspace =>
        {
            Assert.Equal(2, handler.Calls);
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            Assert.False(File.Exists(workspace.RecognitionErrorPath("abc")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StoresTimeoutAsImageErrorWithoutFailingTheJob()
    {
        var handler = new TimeoutHandler(int.MaxValue);

        await RecognizeOnceAsync(handler, 1, async workspace =>
        {
            Assert.Equal(1, handler.Calls);
            Assert.False(File.Exists(workspace.RecognitionPath("abc")));
            var error = await File.ReadAllTextAsync(workspace.RecognitionErrorPath("abc"));
            Assert.Contains("did not respond", error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CallerCancellationIsNotStoredAsImageError()
    {
        var root = Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
        var workspace = JobWorkspace.Create(root, "sample.pdf");
        using var cts = new CancellationTokenSource();
        var handler = new CancelOnSendHandler(cts);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        try
        {
            var relative = Path.Combine("images", "abc.png");
            await File.WriteAllBytesAsync(Path.Combine(workspace.Root, relative), [1, 2, 3, 4]);
            var options = Microsoft.Extensions.Options.Options.Create(new FlexyParseOptions
            {
                ModelApi = new ModelApiOptions
                {
                    ApiKey = "test-key",
                    Model = "google/gemini-2.5-flash",
                    MaxAttempts = 3
                }
            });
            var recognizer = new OpenAiImageRecognizer(new FixedClientFactory(client), options, NullLogger<OpenAiImageRecognizer>.Instance, new MarkdownJsonParser());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognizer.RecognizeAsync(
                workspace,
                new DocumentIndex
                {
                    SourceFileName = "sample.pdf",
                    Metadata = new DocumentMetadata { PageCount = 1 },
                    Images =
                    [
                        new ImageManifestEntry
                        {
                            Hash = "abc",
                            RelativePath = relative,
                            MediaType = "image/png",
                            Width = 2,
                            Height = 2
                        }
                    ]
                },
                cts.Token));

            Assert.False(File.Exists(workspace.RecognitionPath("abc")));
            Assert.False(File.Exists(workspace.RecognitionErrorPath("abc")));
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
    public async Task ReportsTruncatedAnswer()
    {
        var handler = new ScriptedHandler(Chat(System.Text.Json.JsonSerializer.Serialize("### Incomplete"), "length"));

        await RecognizeOnceAsync(handler, 1, async workspace =>
        {
            Assert.False(File.Exists(workspace.RecognitionPath("abc")));
            var error = await File.ReadAllTextAsync(workspace.RecognitionErrorPath("abc"));
            Assert.Contains("enough tokens", error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RetriesNonEmptyTruncatedMarkdownAndStoresOnlyCompleteAnswer()
    {
        var handler = new CompletionTokenHandler(call => call == 1
            ? JsonResponse(Chat(System.Text.Json.JsonSerializer.Serialize("### Incomplete"), "length"))
            : RecognitionResponse());

        await RecognizeOnceAsync(handler, 2, async workspace =>
        {
            Assert.Equal([4096, 8192], handler.TokenLimits);
            await AssertRecognitionAsync(workspace, "### Plan\n\nA box.\n\nStart");
            Assert.False(File.Exists(workspace.RecognitionErrorPath("abc")));
        });
    }

    [Theory]
    [InlineData("content_filter")]
    [InlineData("safety")]
    public async Task DoesNotStoreFilteredContent(string finishReason)
    {
        var handler = new ScriptedHandler(Chat(System.Text.Json.JsonSerializer.Serialize("Filtered response"), finishReason));

        await RecognizeOnceAsync(handler, 1, async workspace =>
        {
            Assert.False(File.Exists(workspace.RecognitionPath("abc")));
            Assert.Contains("refused", await File.ReadAllTextAsync(workspace.RecognitionErrorPath("abc")), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RateLimitDelayHonorsRetryAfterAboveThirtySeconds()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));

        var delay = OpenAiImageRecognizer.RateLimitDelay(response, 1);

        Assert.Equal(TimeSpan.FromSeconds(90), delay);
    }

    [Fact]
    public void RateLimitDelayCapsAtTwoMinutes()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));

        var delay = OpenAiImageRecognizer.RateLimitDelay(response, 1);

        Assert.Equal(TimeSpan.FromMinutes(2), delay);
    }

    [Fact]
    public void RateLimitDelayUsesExponentialBackoffWithoutHeader()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.Equal(TimeSpan.FromSeconds(2), OpenAiImageRecognizer.RateLimitDelay(response, 1));
        Assert.Equal(TimeSpan.FromSeconds(4), OpenAiImageRecognizer.RateLimitDelay(response, 3));
        Assert.Equal(TimeSpan.FromMinutes(2), OpenAiImageRecognizer.RateLimitDelay(response, 10));
    }

    [Fact]
    public void RateLimitDelayHonorsRetryAfterDateAndRaisesShortPausesToTwoSeconds()
    {
        using var future = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        future.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(45));
        using var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-5));
        using var brief = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        brief.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));

        Assert.InRange(OpenAiImageRecognizer.RateLimitDelay(future, 1).TotalSeconds, 40, 46);
        Assert.Equal(TimeSpan.FromSeconds(2), OpenAiImageRecognizer.RateLimitDelay(past, 1));
        Assert.Equal(TimeSpan.FromSeconds(2), OpenAiImageRecognizer.RateLimitDelay(brief, 1));
    }

    [Fact(Timeout = 15000)]
    public async Task WaitsForRetryAfterBeforeRetryingRateLimitedImage()
    {
        var retryAfter = TimeSpan.FromMilliseconds(400);
        var handler = new RateLimitHandler(retryAfter);

        await RecognizeOnceAsync(handler, 2, workspace =>
        {
            Assert.Equal(2, handler.Calls);
            Assert.True(handler.Gap >= retryAfter - TimeSpan.FromMilliseconds(50));
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            return Task.CompletedTask;
        });
    }

    [Fact(Timeout = 15000)]
    public async Task SerializesRetriesAfterSharedRateLimit()
    {
        var handler = new SharedRateLimitHandler(TimeSpan.FromMilliseconds(200));

        await RecognizeImagesAsync(handler, 2, 2, workspace =>
        {
            Assert.Equal(4, handler.Calls);
            Assert.Equal(1, handler.MaxInFlightAfterLimit);
            Assert.True(handler.FirstRetryGap >= TimeSpan.FromMilliseconds(150));
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            Assert.True(File.Exists(workspace.RecognitionPath("def")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DoublesCompletionTokensWhenTheAnswerHitsTheContextLimit()
    {
        var handler = new CompletionTokenHandler(call => call < 3
            ? JsonResponse(Chat("\"\"", "length"))
            : RecognitionResponse());

        await RecognizeOnceAsync(handler, 3, workspace =>
        {
            Assert.Equal([4096, 8192, 16384], handler.TokenLimits);
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DoublesCompletionTokensWhenTheContextWindowIsExceeded()
    {
        var handler = new CompletionTokenHandler(call =>
        {
            if (call == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"This endpoint's maximum context length is 8192 tokens.","code":"context_length_exceeded"}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return RecognitionResponse();
        });

        await RecognizeOnceAsync(handler, 2, workspace =>
        {
            Assert.Equal([4096, 8192], handler.TokenLimits);
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task KeepsCompletionTokensWhenTheAnswerIsEmpty()
    {
        var handler = new CompletionTokenHandler(call => call == 1
            ? JsonResponse(Chat(System.Text.Json.JsonSerializer.Serialize("  ")))
            : RecognitionResponse());

        await RecognizeOnceAsync(handler, 2, _ =>
        {
            Assert.Equal([4096, 4096], handler.TokenLimits);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DropsReasoningDisableWhenTheModelRejectsIt()
    {
        var handler = new ReasoningRejectedHandler();

        await RecognizeOnceAsync(handler, 1, workspace =>
        {
            Assert.Equal(2, handler.Bodies.Count);
            Assert.Contains("\"effort\":\"none\"", handler.Bodies[0], StringComparison.Ordinal);
            Assert.DoesNotContain("\"effort\":\"none\"", handler.Bodies[1], StringComparison.Ordinal);
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LogsDocumentPagesAndActualTokenUsage()
    {
        var logger = new RecordingLogger<OpenAiImageRecognizer>();
        var handler = new ScriptedHandler(Chat("\"# Recognized text\"", usageJson:
            """{"prompt_tokens":120,"completion_tokens":35,"total_tokens":155}"""));

        await RecognizeOnceAsync(handler, 1, workspace =>
        {
            var request = Assert.Single(logger.Entries, entry => entry.Message.StartsWith("Recognizing image", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Information, request.Level);
            Assert.Equal("sample.pdf", request.Properties["File"]);
            Assert.Equal("1", request.Properties["Pages"]);
            Assert.Equal(workspace.JobId, request.Properties["JobId"]);

            var usage = Assert.Single(logger.Entries, entry => entry.Properties.ContainsKey("TotalTokens"));
            Assert.Equal(LogLevel.Information, usage.Level);
            Assert.Equal("sample.pdf", usage.Properties["File"]);
            Assert.Equal("1", usage.Properties["Pages"]);
            Assert.Equal("abc", usage.Properties["Hash"]);
            Assert.Equal(workspace.JobId, usage.Properties["JobId"]);
            Assert.Equal(1, usage.Properties["Attempt"]);
            Assert.Equal(120L, usage.Properties["PromptTokens"]);
            Assert.Equal(35L, usage.Properties["CompletionTokens"]);
            Assert.Equal(155L, usage.Properties["TotalTokens"]);
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("test-key", StringComparison.Ordinal)
                || entry.Message.Contains("# Recognized text", StringComparison.Ordinal));
            return Task.CompletedTask;
        }, logger);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"prompt_tokens\":\"invalid\",\"completion_tokens\":-1,\"total_tokens\":1.5}")]
    public async Task LogsUnavailableUsageWithoutInventingTokenCounts(string? usageJson)
    {
        var logger = new RecordingLogger<OpenAiImageRecognizer>();
        var handler = new ScriptedHandler(Chat("\"Recognized\"", usageJson: usageJson));

        await RecognizeOnceAsync(handler, 1, workspace =>
        {
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            var usage = Assert.Single(logger.Entries, entry => entry.Message.Contains("token usage was not reported", StringComparison.Ordinal));
            Assert.Equal("sample.pdf", usage.Properties["File"]);
            Assert.Equal("1", usage.Properties["Pages"]);
            Assert.DoesNotContain(logger.Entries, entry => entry.Properties.ContainsKey("TotalTokens"));
            return Task.CompletedTask;
        }, logger);
    }

    [Fact]
    public async Task LogsUsageForTruncatedAttemptAndSuccessfulRetrySeparately()
    {
        var logger = new RecordingLogger<OpenAiImageRecognizer>();
        var handler = new ScriptedHandler(
            Chat("\"Incomplete\"", "length", """{"prompt_tokens":100,"completion_tokens":4096,"total_tokens":4196}"""),
            Chat("\"Complete\"", usageJson: """{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150}"""));

        await RecognizeOnceAsync(handler, 2, workspace =>
        {
            Assert.True(File.Exists(workspace.RecognitionPath("abc")));
            var entries = logger.Entries.Where(entry => entry.Properties.ContainsKey("TotalTokens")).ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Equal(1, entries[0].Properties["Attempt"]);
            Assert.Equal(4196L, entries[0].Properties["TotalTokens"]);
            Assert.Equal(2, entries[1].Properties["Attempt"]);
            Assert.Equal(150L, entries[1].Properties["TotalTokens"]);
            return Task.CompletedTask;
        }, logger);
    }

    [Fact]
    public async Task LogsZeroAndPartialUsageWithoutReplacingMissingCounts()
    {
        var logger = new RecordingLogger<OpenAiImageRecognizer>();
        var handler = new ScriptedHandler(Chat("\"Recognized\"", usageJson: """{"completion_tokens":0}"""));

        await RecognizeOnceAsync(handler, 1, _ =>
        {
            var usage = Assert.Single(logger.Entries, entry => entry.Properties.ContainsKey("TotalTokens"));
            Assert.Null(usage.Properties["PromptTokens"]);
            Assert.Equal(0L, usage.Properties["CompletionTokens"]);
            Assert.Null(usage.Properties["TotalTokens"]);
            return Task.CompletedTask;
        }, logger);
    }

    [Fact]
    public async Task BothOutputsUseTheSameParsedRecognitionJson()
    {
        const string answer = "### Plan\n\nText **bold**.\n\n| Col | Qty |\n| --- | --- |\n| A | 2 |\n\n- [x] Done\n\n```csharp\nvar value = 1;\n```";
        var handler = new ScriptedHandler(Chat(JsonSerializer.Serialize(answer)));
        await RecognizeOnceAsync(handler, 1, async workspace =>
        {
            var cache = await File.ReadAllBytesAsync(workspace.RecognitionPath("abc"));
            using var recognized = JsonDocument.Parse(cache);
            var page = new StoredPage
            {
                Number = 1,
                Blocks =
                [
                    new ParagraphBlock { Text = "Before" },
                    new ImageBlock { Id = "p1-i1", ContentHash = "abc" },
                    new ParagraphBlock { Text = "After" }
                ]
            };
            await using (var stream = File.Create(workspace.PagePath(1)))
            {
                await JsonSerializer.SerializeAsync(stream, page, JsonDefaults.Options);
            }

            var index = new DocumentIndex
            {
                SourceFileName = "sample.pdf",
                Metadata = new DocumentMetadata { PageCount = 1 },
                Images = []
            };
            var parser = new MarkdownJsonParser();
            var jsonWriter = new JsonDocumentWriter(parser);
            await using var jsonOutput = new MemoryStream();
            await jsonWriter.WriteAsync(workspace, index, jsonOutput, CancellationToken.None);
            using var json = JsonDocument.Parse(jsonOutput.ToArray());
            var blocks = json.RootElement.GetProperty("pages")[0].GetProperty("blocks");
            Assert.Equal(recognized.RootElement.GetArrayLength() + 2, blocks.GetArrayLength());
            for (var position = 0; position < recognized.RootElement.GetArrayLength(); position++)
            {
                Assert.True(JsonElement.DeepEquals(recognized.RootElement[position], blocks[position + 1]));
            }

            await using var markdownOutput = new MemoryStream();
            await new MarkdownDocumentWriter(jsonWriter, new MarkdownJsonFormatter())
                .WriteAsync(workspace, index, markdownOutput, CancellationToken.None);
            using var parsedOutput = new MemoryStream();
            using (var writer = new Utf8JsonWriter(parsedOutput))
            {
                parser.Write(writer, Encoding.UTF8.GetString(markdownOutput.ToArray()));
            }

            using var markdownJson = JsonDocument.Parse(parsedOutput.ToArray());
            var outputBlocks = markdownJson.RootElement.EnumerateArray().ToArray();
            var first = Array.FindIndex(outputBlocks, block => block.TryGetProperty("text", out var text) && text.GetString() == "Before");
            Assert.True(first >= 0);
            for (var position = 0; position < recognized.RootElement.GetArrayLength(); position++)
            {
                Assert.True(JsonElement.DeepEquals(recognized.RootElement[position], outputBlocks[first + position + 1]));
            }

            Assert.Equal("After", outputBlocks[first + recognized.RootElement.GetArrayLength() + 1].GetProperty("text").GetString());
            Assert.Equal(cache, await File.ReadAllBytesAsync(workspace.RecognitionPath("abc")));
            Assert.Equal(1, handler.Calls);
        });
    }

    private static async Task AssertRecognitionAsync(JobWorkspace workspace, string markdown)
    {
        using var expectedStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(expectedStream))
        {
            new MarkdownJsonParser().Write(writer, markdown);
        }

        using var expected = JsonDocument.Parse(expectedStream.ToArray());
        using var actual = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("abc")));
        Assert.Equal(JsonValueKind.Array, actual.RootElement.ValueKind);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    private static string Chat(string contentJson, string finishReason = "stop", string? usageJson = null)
    {
        var response = "{\"id\":\"chatcmpl-1\",\"choices\":[{\"finish_reason\":\"" + finishReason + "\",\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" + contentJson + "}}],\"created\":1,\"model\":\"google/gemini-2.5-flash\",\"object\":\"chat.completion\"}";
        return usageJson is null ? response : response[..^1] + ",\"usage\":" + usageJson + "}";
    }

    private static Task RecognizeOnceAsync(
        HttpMessageHandler handler,
        int maxAttempts,
        Func<JobWorkspace, Task> assert,
        ILogger<OpenAiImageRecognizer>? logger = null,
        Action<ModelApiOptions>? configure = null)
    {
        return RecognizeImagesAsync(handler, maxAttempts, 1, assert, logger, configure);
    }

    private static async Task RecognizeImagesAsync(
        HttpMessageHandler handler,
        int maxAttempts,
        int imageCount,
        Func<JobWorkspace, Task> assert,
        ILogger<OpenAiImageRecognizer>? logger = null,
        Action<ModelApiOptions>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "flexyparse-tests", Guid.NewGuid().ToString("N"));
        var workspace = JobWorkspace.Create(root, "sample.pdf");
        var settings = new ModelApiOptions
        {
            ApiKey = "test-key",
            Model = "google/gemini-2.5-flash",
            MaxAttempts = maxAttempts
        };
        configure?.Invoke(settings);
        var client = new HttpClient(handler) { BaseAddress = settings.ResolveBaseAddress() };
        try
        {
            var images = new ImageManifestEntry[imageCount];
            for (var index = 0; index < imageCount; index++)
            {
                var hash = index == 0 ? "abc" : "def";
                var relative = Path.Combine("images", hash + ".png");
                await File.WriteAllBytesAsync(Path.Combine(workspace.Root, relative), [1, 2, 3, 4]);
                images[index] = new ImageManifestEntry
                {
                    Hash = hash,
                    PageNumbers = [1],
                    RelativePath = relative,
                    MediaType = "image/png",
                    Width = 2,
                    Height = 2
                };
            }

            var options = Microsoft.Extensions.Options.Options.Create(new FlexyParseOptions
            {
                MaxConcurrentImageRecognitions = Math.Max(1, imageCount),
                ModelApi = settings
            });
            var recognizer = new OpenAiImageRecognizer(new FixedClientFactory(client), options, logger ?? NullLogger<OpenAiImageRecognizer>.Instance, new MarkdownJsonParser());
            await recognizer.RecognizeAsync(
                workspace,
                new DocumentIndex
                {
                    SourceFileName = "sample.pdf",
                    Metadata = new DocumentMetadata { PageCount = 1 },
                    Images = images
                },
                CancellationToken.None);
            await assert(workspace);
        }
        finally
        {
            client.Dispose();
            await workspace.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            var payload = responses[Math.Min(_index, responses.Length - 1)];
            if (_index < responses.Length)
            {
                _index++;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ReasoningRejectedHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Bodies.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"Reasoning is mandatory for this endpoint and cannot be disabled."}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return RecognitionResponse();
        }
    }

    private sealed class RateLimitHandler(TimeSpan retryAfter) : HttpMessageHandler
    {
        private int _calls;
        private long _limitedAtTicks;
        private long _retryTicks;

        public int Calls => Volatile.Read(ref _calls);

        public TimeSpan Gap
        {
            get
            {
                var limited = Interlocked.Read(ref _limitedAtTicks);
                var retry = Interlocked.Read(ref _retryTicks);
                if (limited == 0 || retry == 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromTicks(retry - limited);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (call == 1)
            {
                Volatile.Write(ref _limitedAtTicks, DateTime.UtcNow.Ticks);
                return RateLimited(retryAfter);
            }

            Interlocked.CompareExchange(ref _retryTicks, DateTime.UtcNow.Ticks, 0);
            return RecognitionResponse();
        }
    }

    private sealed class SharedRateLimitHandler(TimeSpan retryAfter) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _entered;
        private int _inFlight;
        private int _maxInFlight;
        private long _releasedTicks;
        private long _firstRetryTicks;

        public int Calls => Volatile.Read(ref _calls);

        public int MaxInFlightAfterLimit => Volatile.Read(ref _maxInFlight);

        public TimeSpan FirstRetryGap
        {
            get
            {
                var released = Interlocked.Read(ref _releasedTicks);
                var retry = Interlocked.Read(ref _firstRetryTicks);
                if (released == 0 || retry == 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromTicks(retry - released);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            var entered = Interlocked.Increment(ref _entered);
            Interlocked.Increment(ref _calls);
            if (entered == 2)
            {
                _bothStarted.TrySetResult();
            }

            if (entered <= 2)
            {
                await _bothStarted.Task.WaitAsync(cancellationToken);
                NoteRelease();
                return RateLimited(retryAfter);
            }

            NoteFirstRetry();
            var flight = Interlocked.Increment(ref _inFlight);
            try
            {
                NoteMaxFlight(flight);
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
                return RecognitionResponse();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void NoteRelease()
        {
            var released = DateTime.UtcNow.Ticks;
            long observed;
            do
            {
                observed = Interlocked.Read(ref _releasedTicks);
                if (observed != 0 && observed <= released)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _releasedTicks, released, observed) != observed);
        }

        private void NoteFirstRetry()
        {
            Interlocked.CompareExchange(ref _firstRetryTicks, DateTime.UtcNow.Ticks, 0);
        }

        private void NoteMaxFlight(int flight)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxInFlight);
                if (flight <= observed)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maxInFlight, flight, observed) != observed);
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CompletionTokenHandler(Func<int, HttpResponseMessage> next) : HttpMessageHandler
    {
        public List<int> TokenLimits { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(body);
            TokenLimits.Add(document.RootElement.GetProperty("max_completion_tokens").GetInt32());
            return next(TokenLimits.Count);
        }
    }

    private static HttpResponseMessage RecognitionResponse()
    {
        var content = Chat(System.Text.Json.JsonSerializer.Serialize("### Plan\n\nA box.\n\nStart"));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage RateLimited(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"error":{"message":"Rate limit exceeded"}}""",
                Encoding.UTF8,
                "application/json")
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private sealed class TimeoutHandler(int failuresBeforeSuccess) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (call <= failuresBeforeSuccess)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 180 seconds elapsing.");
            }

            return RecognitionResponse();
        }
    }

    private sealed class CancelOnSendHandler(CancellationTokenSource source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            source.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? ApplicationTitle { get; private set; }

        public string? Referer { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            RequestUri = request.RequestUri;
            ApplicationTitle = request.Headers.TryGetValues("X-Title", out var titles) ? titles.Single() : null;
            Referer = request.Headers.TryGetValues("HTTP-Referer", out var referers) ? referers.Single() : null;
            var content = """
                {"id":"chatcmpl-1","choices":[{"finish_reason":"stop","index":0,"message":{"role":"assistant","content":"```markdown\n### Plan\n\nA box.\n\nStart\n```"}}],"created":1,"model":"google/gemini-2.5-flash","object":"chat.completion"}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }
}
