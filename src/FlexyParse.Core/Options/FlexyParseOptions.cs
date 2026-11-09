namespace FlexyParse.Options;

public sealed class FlexyParseOptions
{
    public const string SectionName = "FlexyParse";

    /// <summary>
    /// How many PDF documents may be parsed at the same time.
    /// Zero selects the processor count.
    /// </summary>
    public int MaxConcurrentDocuments { get; set; } = 2;

    public int MaxConcurrentImageRecognitions { get; set; } = 4;

    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public int MaxFilesPerRequest { get; set; } = 16;

    public bool RecognizeImages { get; set; } = true;

    /// <summary>Preserves conversion workspaces and Host results on disk for debugging.</summary>
    public bool KeepResults { get; set; }

    public string GotenbergUrl { get; set; } = "";

    public int GotenbergTimeoutSeconds { get; set; } = 180;

    public ImageRenderFallbackOptions ImageRenderFallback { get; set; } = new();

    public ModelApiOptions ModelApi { get; set; } = new();

    public RecognitionOptions Recognition { get; set; } = new();

    public ImageLayoutOptions ImageLayout { get; set; } = new();

    public ImageInputOptions ImageInput { get; set; } = new();

    public ImageRecognitionFilterOptions ImageRecognitionFilter { get; set; } = new();

    /// <summary>
    /// Uses an application-specific subdirectory of the system temporary directory.
    /// </summary>
    public string ResolveTempDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "flexyparse");
    }

    public int ResolveDocumentParallelism()
    {
        if (MaxConcurrentDocuments > 0)
        {
            return MaxConcurrentDocuments;
        }

        return Math.Max(1, Environment.ProcessorCount);
    }

    public int ResolveImageParallelism()
    {
        return Math.Max(1, MaxConcurrentImageRecognitions);
    }
}

public sealed class ImageRenderFallbackOptions
{
    public bool Enabled { get; set; } = true;

    public int Dpi { get; set; } = 200;

    public int MaxPixels { get; set; } = 16_000_000;

    public int MaxDimension { get; set; } = 8192;
}

public sealed class ModelApiOptions
{
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1/";

    public string ApiKey { get; set; } = "";

    public bool RequireApiKey { get; set; } = true;

    public string Model { get; set; } = "google/gemini-2.5-flash";

    public int TimeoutSeconds { get; set; } = 180;

    public int MaxAttempts { get; set; } = 3;

    public int MaxCompletionTokens { get; set; } = 4096;

    public string TokenLimitParameter { get; set; } = "max_completion_tokens";

    public double? Temperature { get; set; }

    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;

    public string Title { get; set; } = "FlexyParse";

    public string HttpReferer { get; set; } = "";

    internal Uri ResolveBaseAddress()
    {
        var baseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? "https://openrouter.ai/api/v1/" : BaseUrl.Trim();
        var address = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute);
        if ((address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
            || address.Query.Length != 0 || address.Fragment.Length != 0 || address.UserInfo.Length != 0)
        {
            throw new ArgumentException("ModelApi:BaseUrl must be an HTTP(S) API base URL without credentials, query or fragment.", nameof(BaseUrl));
        }

        return address;
    }

    internal bool UsesProviderExtensions()
    {
        var address = ResolveBaseAddress();
        return address.Scheme == Uri.UriSchemeHttps
            && address.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            && address.IsDefaultPort
            && address.AbsolutePath == "/api/v1/";
    }
}
