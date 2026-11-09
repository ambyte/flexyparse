using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Storage;
using Microsoft.Extensions.Options;

namespace FlexyParse.Hosting;

/// <summary>Stages Docling HTTP and Base64 sources for the existing conversion pipeline.</summary>
public sealed class SourceDocumentService(IHttpClientFactory clients, IOptions<SourceDownloadOptions> options)
{
    public const string HttpClientName = "DocumentSources";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static IServiceCollection AddServices(IServiceCollection services)
    {
        services.AddOptions<SourceDownloadOptions>()
            .BindConfiguration("FlexyParse:Sources")
            .Validate(settings => settings.TimeoutSeconds > 0, "Sources:TimeoutSeconds must be positive.")
            .ValidateOnStart();
        services.AddTransient<SourceDocumentService>();
        services.AddHttpClient(HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            // Source URLs may contain signed query strings; do not put them into HTTP client logs.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ConnectCallback = ConnectPublicAddressAsync
            });
        return services;
    }

    internal async Task<ConversionBatch> ReadAsync(HttpRequest request, FlexyParseOptions settings, CancellationToken token)
    {
        var batch = await StageAsync(request, settings, token).ConfigureAwait(false);
        try
        {
            await ResolveAsync(batch, settings, token).ConfigureAwait(false);
            return batch;
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    // Async submissions retain the JSON file, not its potentially large Base64 strings or HttpContext.
    internal async Task<ConversionBatch> StageAsync(HttpRequest request, FlexyParseOptions settings, CancellationToken token)
    {
        if (!request.HasJsonContentType())
        {
            throw ConversionBatch.Invalid("application/json is expected.");
        }

        var batch = new ConversionBatch(settings);
        try
        {
            // Bound the JSON body even when Content-Length is absent. Stage on disk rather than
            // keeping both the raw JSON and its deserialized Base64 strings in memory.
            var bodyPath = batch.NewPath();
            var maxBodyBytes = checked((settings.MaxUploadBytes / 3 + 1) * 4 * settings.MaxFilesPerRequest + 1024 * 1024);
            await using (var body = File.Create(bodyPath))
            {
                await LimitedStreamCopy.CopyAsync(request.Body, body, maxBodyBytes, token).ConfigureAwait(false);
            }

            batch.SourceRequestPath = bodyPath;
            var payload = await ReadPayloadAsync(batch, token).ConfigureAwait(false);
            IgnoredConversionParameters.LogAndClear(request, payload.UnsupportedParameters);
            IgnoredConversionParameters.LogAndClear(request, payload.Options?.UnsupportedParameters, "options.");
            if (payload.HttpSources is not null)
            {
                for (var index = 0; index < payload.HttpSources.Length; index++)
                {
                    IgnoredConversionParameters.LogAndClear(request, payload.HttpSources[index]?.UnsupportedParameters, $"http_sources[{index}].");
                }
            }
            if (payload.FileSources is not null)
            {
                for (var index = 0; index < payload.FileSources.Length; index++)
                {
                    IgnoredConversionParameters.LogAndClear(request, payload.FileSources[index]?.UnsupportedParameters, $"file_sources[{index}].");
                }
            }
            if (payload.Options is null || payload.HttpSources is null || payload.FileSources is null
                || payload.HttpSources.Length + (long)payload.FileSources.Length is 0
                || payload.HttpSources.Length + (long)payload.FileSources.Length > settings.MaxFilesPerRequest)
            {
                throw ConversionBatch.Invalid("Invalid number of sources or options.");
            }

            payload.Options.Validate();
            batch.Options = payload.Options;
            // Validate source shape before accepting the task; network and decoding run in the worker.
            foreach (var source in payload.HttpSources)
            {
                _ = SourceUri(source);
            }
            foreach (var source in payload.FileSources)
            {
                ValidateFileSource(source);
            }
            return batch;
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    internal async Task ResolveAsync(ConversionBatch batch, FlexyParseOptions settings, CancellationToken token)
    {
        var payload = await ReadPayloadAsync(batch, token).ConfigureAwait(false);
        File.Delete(batch.SourceRequestPath!);
        batch.SourceRequestPath = null;
        foreach (var source in payload.HttpSources)
        {
            var uri = SourceUri(source);
            var path = batch.NewPath();
            await DownloadAsync(uri, path, settings.MaxUploadBytes, token).ConfigureAwait(false);
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            batch.Files.Add(new UploadedFile(FileNameSanitizer.Sanitize(string.IsNullOrWhiteSpace(name) ? "document.pdf" : name), path));
        }

        foreach (var source in payload.FileSources)
        {
            ValidateFileSource(source);
            var path = batch.NewPath();
            await DecodeBase64Async(source!.Base64String!, path, settings.MaxUploadBytes, token).ConfigureAwait(false);
            batch.Files.Add(new UploadedFile(FileNameSanitizer.Sanitize(source.Filename), path));
        }
    }

    private static async Task<SourceRequest> ReadPayloadAsync(ConversionBatch batch, CancellationToken token)
    {
        await using var body = File.OpenRead(batch.SourceRequestPath!);
        return await JsonSerializer.DeserializeAsync<SourceRequest>(body, JsonOptions, token).ConfigureAwait(false)
            ?? throw ConversionBatch.Invalid("The sources were not cited.");
    }

    private static Uri SourceUri(HttpSource? source)
    {
        if (source is null || !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback)
        {
            throw ConversionBatch.Invalid("The source must be a public HTTP(S) URL without credentials and fragment.");
        }
        return uri;
    }

    private static void ValidateFileSource(FileSource? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Filename) || string.IsNullOrEmpty(source.Base64String))
        {
            throw ConversionBatch.Invalid("file_sources requires filename and base64_string.");
        }
    }

    private async Task DownloadAsync(Uri uri, string path, long maxBytes, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw ConversionBatch.Invalid("Failed to download HTTP source. Redirects are not supported.");
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new DocumentConversionException(ConversionStatus.PayloadTooLarge, "HTTP source exceeds the allowed size.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = File.Create(path);
            await LimitedStreamCopy.CopyAsync(input, output, maxBytes, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw ConversionBatch.Invalid("HTTP source is not accessible or its URL is forbidden.");
        }
        catch (IOException)
        {
            throw ConversionBatch.Invalid("Failed to read HTTP source.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new DocumentConversionException(StatusCodes.Status504GatewayTimeout, "The time for downloading the HTTP source has expired.");
        }
    }

    private static async Task DecodeBase64Async(string encoded, string path, long maxBytes, CancellationToken token)
    {
        // Decode bounded chunks, keeping the additional memory independent of document size.
        var chars = new char[16 * 1024];
        var bytes = new byte[12 * 1024];
        var count = 0;
        long total = 0;
        var ended = false;
        await using var output = File.Create(path);
        for (var index = 0; index < encoded.Length; index++)
        {
            if (index % chars.Length == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var character = encoded[index];
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            if (ended)
            {
                throw ConversionBatch.Invalid("Invalid base64_string.");
            }

            chars[count++] = character;
            if (count != chars.Length && index != encoded.Length - 1)
            {
                continue;
            }

            await FlushAsync().ConfigureAwait(false);
        }

        if (count > 0)
        {
            await FlushAsync().ConfigureAwait(false);
        }

        async Task FlushAsync()
        {
            token.ThrowIfCancellationRequested();
            if (!Convert.TryFromBase64Chars(chars.AsSpan(0, count), bytes, out var written))
            {
                throw ConversionBatch.Invalid("Invalid base64_string.");
            }

            ended = chars.AsSpan(0, count).Contains('=');
            total += written;
            if (total > maxBytes)
            {
                throw new DocumentConversionException(ConversionStatus.PayloadTooLarge, "Base64 source exceeds the allowed size.");
            }

            await output.WriteAsync(bytes.AsMemory(0, written), token).ConfigureAwait(false);
            count = 0;
        }
    }

    private static async ValueTask<Stream> ConnectPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        // Validate and pin the actual destination at connect time (prevents DNS rebinding).
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException("Source address is not public.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Only global unicast; exclude transition/documentation ranges that can tunnel IPv4.
            return (bytes[0] & 0xe0) == 0x20
                && !(bytes[0] == 0x20 && bytes[1] == 0x02)
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 0x20);
        }

        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 || bytes[1] == 2))
            && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100))
            && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    private sealed class SourceRequest
    {
        public HttpSource?[] HttpSources { get; init; } = [];
        public FileSource?[] FileSources { get; init; } = [];
        public ConversionOptions Options { get; init; } = new();

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnsupportedParameters { get; init; }
    }

    private sealed class HttpSource
    {
        public string? Url { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnsupportedParameters { get; init; }
    }

    private sealed class FileSource
    {
        public string? Filename { get; init; }
        public string? Base64String { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnsupportedParameters { get; init; }
    }
}

public sealed class SourceDownloadOptions
{
    public int TimeoutSeconds { get; set; } = 180;
}
