using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace FlexyParse.Hosting;

internal static class HostConfiguration
{
    private static readonly (string Variable, string Key)[] EnvironmentAliases =
    [
        ("FLEXYPARSE_API_KEY", "FlexyParse:Host:ApiKey"),
        ("FLEXYPARSE_MAX_CONCURRENT_DOCUMENTS", "FlexyParse:MaxConcurrentDocuments"),
        ("FLEXYPARSE_MAX_CONCURRENT_IMAGE_RECOGNITIONS", "FlexyParse:MaxConcurrentImageRecognitions"),
        ("FLEXYPARSE_MAX_UPLOAD_BYTES", "FlexyParse:MaxUploadBytes"),
        ("FLEXYPARSE_MAX_FILES_PER_REQUEST", "FlexyParse:MaxFilesPerRequest"),
        ("FLEXYPARSE_RECOGNIZE_IMAGES", "FlexyParse:RecognizeImages"),
        ("FLEXYPARSE_RECOGNITION_PROVIDER", "FlexyParse:Recognition:Provider"),
        ("FLEXYPARSE_NEMOTRON_GPU_DEVICE_ID", "FlexyParse:Recognition:Nemotron:GpuDeviceId"),
        ("FLEXYPARSE_NEMOTRON_INFERENCE_THREADS", "FlexyParse:Recognition:Nemotron:InferenceThreads"),
        ("FLEXYPARSE_RAPIDOCR_LANGUAGE", "FlexyParse:Recognition:Language"),
        ("FLEXYPARSE_KEEP_RESULTS", "FlexyParse:KeepResults"),
        ("FLEXYPARSE_IMAGE_LAYOUT_ENABLED", "FlexyParse:ImageLayout:Enabled"),
        ("FLEXYPARSE_GOTENBERG_URL", "FlexyParse:GotenbergUrl"),
        ("FLEXYPARSE_GOTENBERG_TIMEOUT_SECONDS", "FlexyParse:GotenbergTimeoutSeconds"),
        ("FLEXYPARSE_ASYNC_WORKER_COUNT", "FlexyParse:Async:WorkerCount"),
        ("FLEXYPARSE_ASYNC_QUEUE_CAPACITY", "FlexyParse:Async:QueueCapacity"),
        ("FLEXYPARSE_ASYNC_MAX_STORED_TASKS", "FlexyParse:Async:MaxStoredTasks"),
        ("FLEXYPARSE_ASYNC_RESULT_RETENTION_SECONDS", "FlexyParse:Async:ResultRetentionSeconds"),
        ("FLEXYPARSE_ASYNC_CLEANUP_INTERVAL_SECONDS", "FlexyParse:Async:CleanupIntervalSeconds"),
        ("FLEXYPARSE_ASYNC_MAX_WEBSOCKET_CONNECTIONS", "FlexyParse:Async:MaxWebSocketConnections"),
        ("FLEXYPARSE_MODEL_API_KEY", "FlexyParse:ModelApi:ApiKey"),
        ("FLEXYPARSE_MODEL_API_BASE_URL", "FlexyParse:ModelApi:BaseUrl"),
        ("FLEXYPARSE_MODEL_API_REQUIRE_API_KEY", "FlexyParse:ModelApi:RequireApiKey"),
        ("FLEXYPARSE_MODEL_API_MODEL", "FlexyParse:ModelApi:Model"),
        ("FLEXYPARSE_MODEL_API_TIMEOUT_SECONDS", "FlexyParse:ModelApi:TimeoutSeconds"),
        ("FLEXYPARSE_MODEL_API_MAX_ATTEMPTS", "FlexyParse:ModelApi:MaxAttempts"),
        ("FLEXYPARSE_MODEL_API_MAX_COMPLETION_TOKENS", "FlexyParse:ModelApi:MaxCompletionTokens"),
        ("FLEXYPARSE_MODEL_API_TOKEN_LIMIT_PARAMETER", "FlexyParse:ModelApi:TokenLimitParameter"),
        ("FLEXYPARSE_MODEL_API_TEMPERATURE", "FlexyParse:ModelApi:Temperature"),
        ("FLEXYPARSE_MODEL_API_MAX_IMAGE_BYTES", "FlexyParse:ModelApi:MaxImageBytes"),
        ("FLEXYPARSE_MODEL_API_TITLE", "FlexyParse:ModelApi:Title"),
        ("FLEXYPARSE_MODEL_API_HTTP_REFERER", "FlexyParse:ModelApi:HttpReferer")
    ];

    internal static void Apply(ConfigurationManager configuration, Func<string, string?>? readEnvironmentVariable = null)
    {
        readEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var legacyKey = readEnvironmentVariable("MODEL_API_KEY");
        if (!string.IsNullOrWhiteSpace(legacyKey))
        {
            values["FlexyParse:ModelApi:ApiKey"] = legacyKey;
        }

        foreach (var (variable, key) in EnvironmentAliases)
        {
            // Absence uses the lower-priority value; an explicit empty value clears it.
            if (readEnvironmentVariable(variable) is { } value)
            {
                values[key] = value;
            }
        }

        // WebApplicationBuilder adds an early command-line source for host settings,
        // before JSON, and a final one for application settings. Do not insert aliases
        // before the early source: JSON defaults would then overwrite the aliases.
        var index = 0;
        while (index < configuration.Sources.Count
            && configuration.Sources[index] is not EnvironmentVariablesConfigurationSource { Prefix: null or "" })
        {
            index++;
        }

        if (index == configuration.Sources.Count)
        {
            // Minimal/custom builders may omit the standard environment source.
            for (var candidate = configuration.Sources.Count - 1; candidate >= 0; candidate--)
            {
                if (configuration.Sources[candidate] is CommandLineConfigurationSource)
                {
                    index = candidate;
                    break;
                }
            }
        }

        configuration.Sources.Insert(index, new MemoryConfigurationSource { InitialData = values });
    }
}
