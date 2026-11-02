using System.Security.Cryptography;
using FlexyParse.Options;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlexyParse.RapidOcr;

/// <summary>Resolved local files for one language. No download URLs are accepted from callers.</summary>
public sealed record RapidOcrModelPaths(string DetectionModelPath, string ClassificationModelPath,
    string RecognitionModelPath, string DictionaryPath);

/// <summary>Provisions only the requested pinned profile, verifying cached assets before reuse.</summary>
public sealed class RapidOcrModelStore : IDisposable
{
    public const string HttpClientName = "rapidocr-model";
    private readonly TimeSpan _downloadTimeout;
    private readonly IHttpClientFactory _clientFactory;
    private readonly RecognitionOptions _options;
    private readonly RapidOcrOptions _localOptions;
    private readonly ILogger<RapidOcrModelStore> _logger;
    private readonly Func<string, RapidOcrModelPreset> _presets;
    // A singleton gate also deduplicates shared detector/classifier downloads across job profiles.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RapidOcrModelStore(IHttpClientFactory clientFactory, IOptions<RecognitionOptions> options,
        IOptions<RapidOcrOptions> localOptions, ILogger<RapidOcrModelStore> logger)
        : this(clientFactory, options, localOptions, logger, RapidOcrModelPresets.Get) { }

    internal RapidOcrModelStore(IHttpClientFactory clientFactory, IOptions<RecognitionOptions> options,
        IOptions<RapidOcrOptions> localOptions, ILogger<RapidOcrModelStore> logger,
        Func<string, RapidOcrModelPreset> presets, TimeSpan? downloadTimeout = null)
    {
        _clientFactory = clientFactory;
        _options = options.Value;
        _localOptions = localOptions.Value;
        _logger = logger;
        _presets = presets;
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Returns resolved paths after provisioning; returns null without I/O for non-RapidOCR providers.</summary>
    public async Task<RapidOcrModelPaths?> EnsureAvailableAsync(string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Provider != RecognitionProvider.RapidOcr)
        {
            return null;
        }

        var name = OcrLanguageProfiles.Normalize(language);
        var local = _localOptions;
        var profile = local.GetProfile(name);
        if (string.IsNullOrWhiteSpace(profile.RecognitionModelPath) != string.IsNullOrWhiteSpace(profile.DictionaryPath))
        {
            throw new InvalidOperationException($"RapidOCR profile '{name}' custom model and dictionary must be configured together.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preset = _presets(name);
            return new(
                await ResolveAsync(local.DetectionModelPath, preset.Detection, cancellationToken).ConfigureAwait(false),
                await ResolveAsync(local.ClassificationModelPath, preset.Classification, cancellationToken).ConfigureAwait(false),
                await ResolveAsync(profile.RecognitionModelPath, preset.Recognition, cancellationToken).ConfigureAwait(false),
                await ResolveAsync(profile.DictionaryPath, preset.Dictionary, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidOperationException($"RapidOCR provisioning failed for profile '{name}'. Check model cache permissions, network access, or configured local overrides.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ResolveAsync(string customPath, RapidOcrModelAsset asset, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (!File.Exists(customPath))
            {
                throw new FileNotFoundException("A configured RapidOCR local override does not exist.", customPath);
            }

            return Path.GetFullPath(customPath);
        }

        var path = Path.GetFullPath(Path.Combine(_localOptions.ModelsDirectory, asset.FileName));
        if (await IsVerifiedAsync(path, asset, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Using verified cached RapidOCR asset {ModelFile}.", asset.FileName);
            return path;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_downloadTimeout);
        try
        {
            _logger.LogInformation("Downloading pinned RapidOCR asset {ModelFile}.", asset.FileName);
            using var client = _clientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" }
                || response.Content.Headers.ContentLength is <= 0
                || response.Content.Headers.ContentLength > asset.MaxBytes)
            {
                throw new InvalidDataException("RapidOCR download has an invalid transport or content length.");
            }

            var progress = new ModelDownloadProgress(_logger, "RapidOCR", asset.FileName, response.Content.Headers.ContentLength);
            long downloadedBytes;
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await LimitedStreamCopy.CopyAsync(source, destination, asset.MaxBytes, timeout.Token, progress.Report).ConfigureAwait(false);
                await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength is { } expected && destination.Length != expected)
                {
                    throw new InvalidDataException("RapidOCR download was incomplete.");
                }

                downloadedBytes = destination.Length;
            }

            progress.Complete(downloadedBytes);
            _logger.LogInformation("Verifying SHA-256 for RapidOCR asset {ModelFile}.", asset.FileName);
            if (!await IsVerifiedAsync(temporary, asset, timeout.Token).ConfigureAwait(false))
            {
                throw new InvalidDataException("RapidOCR download does not match its pinned SHA-256 checksum.");
            }

            timeout.Token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            _logger.LogInformation("RapidOCR asset {ModelFile} downloaded and verified.", asset.FileName);
            return path;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task<bool> IsVerifiedAsync(string path, RapidOcrModelAsset asset, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > asset.MaxBytes)
        {
            return false;
        }

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _gate.Dispose();
}
