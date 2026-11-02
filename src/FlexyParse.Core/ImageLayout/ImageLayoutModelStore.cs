using System.Security.Cryptography;
using FlexyParse.Options;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlexyParse.ImageLayout;

/// <summary>Downloads a revision-pinned model and commits it only after SHA-256 verification.</summary>
public sealed class ImageLayoutModelStore(
    IHttpClientFactory clientFactory, IOptions<FlexyParseOptions> options, ILogger<ImageLayoutModelStore> logger) : IDisposable
{
    public const string HttpClientName = "image-layout-model";
    internal const long MaxDownloadBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private readonly ImageLayoutOptions _options = options.Value.ImageLayout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
        {
            return;
        }

        if (!_options.IsValid())
        {
            throw new InvalidOperationException("Image layout model download settings are invalid.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.GetFullPath(_options.ModelPath);
            if (await IsVerifiedAsync(path, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("Using verified cached image layout model at {ModelPath}.", path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            try
            {
                logger.LogInformation("Downloading the preset DocLayNet image layout model to {ModelPath}.", path);
                using var client = clientFactory.CreateClient(HttpClientName);
                using var response = await client.GetAsync(_options.ModelDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is { Scheme: not "https" }
                    || response.Content.Headers.ContentLength is <= 0 or > MaxDownloadBytes)
                {
                    throw new InvalidDataException("The layout model download has an invalid transport or content length.");
                }

                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    await LimitedStreamCopy.CopyAsync(source, destination, MaxDownloadBytes, timeout.Token).ConfigureAwait(false);
                    await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
                    if (response.Content.Headers.ContentLength is { } expected && destination.Length != expected)
                    {
                        throw new InvalidDataException("The layout model download was incomplete.");
                    }
                }

                if (!await IsVerifiedAsync(temporary, timeout.Token).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The downloaded layout model does not match its expected SHA-256 checksum.");
                }

                timeout.Token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                logger.LogInformation("Image layout model downloaded and verified at {ModelPath}.", path);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> IsVerifiedAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaxDownloadBytes)
        {
            return false;
        }

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(_options.ModelSha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _gate.Dispose();
}
