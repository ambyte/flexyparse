using System.Security.Cryptography;
using System.Text.Json;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlexyParse.Nemotron;

/// <summary>Provisions pinned, integrity-checked INT8/FP16 packs, including ONNX external data.</summary>
public sealed class NemotronModelStore : IDisposable
{
    public const string HttpClientName = "FlexyParse.NemotronModels";
    public const string Revision = "275c486f491cada14e5346c95c9e511543d6dd04";
    private readonly IHttpClientFactory _clients;
    private readonly NemotronOptions _options;
    private readonly ILogger<NemotronModelStore> _logger;
    private readonly IReadOnlyList<ModelFile> _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _ready;

    public NemotronModelStore(IHttpClientFactory clients, IOptions<NemotronOptions> options,
        ILogger<NemotronModelStore> logger) : this(clients, options, logger, ReadPack(options.Value.ExecutionMode)) { }

    internal NemotronModelStore(IHttpClientFactory clients, IOptions<NemotronOptions> options,
        ILogger<NemotronModelStore> logger, IReadOnlyList<ModelFile> files)
    {
        _clients = clients;
        _options = options.Value;
        _logger = logger;
        _files = files;
    }

    internal static IReadOnlyList<ModelFile> ReadPack(NemotronExecutionMode mode = NemotronExecutionMode.CpuInt8)
    {
        var variant = mode switch
        {
            NemotronExecutionMode.CpuInt8 => "int8",
            NemotronExecutionMode.WebGpuFp16 => "fp16",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        using var stream = typeof(NemotronModelStore).Assembly.GetManifestResourceStream(
            $"FlexyParse.Recognition.Nemotron.{variant}-pack.json")
            ?? throw new InvalidOperationException("Missing embedded Nemotron pack manifest.");
        return JsonSerializer.Deserialize<ModelFile[]>(stream)!;
    }

    public async Task<string> EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready is not null) return _ready;
            var directory = Path.GetFullPath(_options.ResolvedModelsDirectory);
            Directory.CreateDirectory(directory);
            _logger.LogInformation("Checking Nemotron {Variant} model pack ({ModelFileCount} files).", _options.PackVariant, _files.Count);
            foreach (var file in _files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, file.Name);
                if (await VerifyAsync(path, file, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Using verified cached Nemotron {Variant} file {ModelFile}.", _options.PackVariant, file.Name);
                    continue;
                }
                if (!_options.DownloadModels)
                    throw new InvalidDataException($"Nemotron {_options.PackVariant} file {file.Name} is missing or corrupt; automatic downloads are disabled.");

                _logger.LogInformation("Downloading Nemotron {Variant} file {ModelFile} ({ModelBytes} bytes).", _options.PackVariant, file.Name, file.Size);
                await DownloadAsync(path, file, cancellationToken).ConfigureAwait(false);
            }

            _ready = directory;
            return directory;
        }
        finally { _gate.Release(); }
    }

    private async Task DownloadAsync(string path, ModelFile file, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds));
        var token = timeout.Token;
        try
        {
            using var client = _clients.CreateClient(HttpClientName);
            using var response = await client.GetAsync(
                $"https://huggingface.co/XDcobra/Nemotron-3.5-Parse-ONNX/resolve/{Revision}/onnx/{_options.PackVariant}/{file.Name}",
                HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != file.Size)
                throw new InvalidDataException($"Unexpected size for Nemotron file {file.Name}.");
            var progress = new ModelDownloadProgress(_logger, $"Nemotron {_options.PackVariant}", file.Name, file.Size);
            long total = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > file.Size) throw new InvalidDataException("Nemotron download exceeds pinned file size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    progress.Report(total);
                }
            }

            progress.Complete(total);
            _logger.LogInformation("Verifying SHA-256 for Nemotron {Variant} file {ModelFile}.", _options.PackVariant, file.Name);
            if (!await VerifyAsync(temporary, file, token).ConfigureAwait(false))
                throw new InvalidDataException($"SHA-256 verification failed for Nemotron file {file.Name}.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            _logger.LogInformation("Nemotron {Variant} file {ModelFile} downloaded and verified.", _options.PackVariant, file.Name);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<bool> VerifyAsync(string path, ModelFile file, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _gate.Dispose();

    internal sealed record ModelFile(string Name, long Size, string Sha256);
}
