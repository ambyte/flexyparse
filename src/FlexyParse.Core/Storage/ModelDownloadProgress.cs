using Microsoft.Extensions.Logging;

namespace FlexyParse.Storage;

/// <summary>Synchronous, throttled download reporting shared by local model providers.</summary>
public sealed class ModelDownloadProgress
{
    private const double BytesPerMiB = 1024d * 1024;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(2);
    private readonly ILogger _logger;
    private readonly string _provider;
    private readonly string _file;
    private readonly long? _totalBytes;
    private readonly TimeProvider _time;
    private long _lastReport;

    public ModelDownloadProgress(ILogger logger, string provider, string file, long? totalBytes)
        : this(logger, provider, file, totalBytes, TimeProvider.System) { }

    internal ModelDownloadProgress(ILogger logger, string provider, string file, long? totalBytes, TimeProvider time)
    {
        _logger = logger;
        _provider = provider;
        _file = file;
        _totalBytes = totalBytes is > 0 ? totalBytes : null;
        _time = time;
        _lastReport = time.GetTimestamp();
        Write(0);
    }

    /// <summary>Called after bytes have been written; does not schedule background callbacks.</summary>
    public void Report(long downloadedBytes)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        var now = _time.GetTimestamp();
        if (_time.GetElapsedTime(_lastReport, now) < ReportInterval) return;
        _lastReport = now;
        Write(downloadedBytes);
    }

    /// <summary>Reports the final byte count before integrity verification, even for small files.</summary>
    public void Complete(long downloadedBytes) => Write(downloadedBytes);

    private void Write(long downloadedBytes)
    {
        if (_totalBytes is { } total)
        {
            _logger.LogInformation(
                "{Provider} download {ModelFile}: {DownloadedMiB:F2} / {TotalMiB:F2} MiB ({DownloadPercent:F1}%).",
                _provider, _file, downloadedBytes / BytesPerMiB, total / BytesPerMiB, downloadedBytes * 100d / total);
        }
        else
        {
            _logger.LogInformation("{Provider} download {ModelFile}: {DownloadedMiB:F2} MiB (total size unknown).",
                _provider, _file, downloadedBytes / BytesPerMiB);
        }
    }
}
