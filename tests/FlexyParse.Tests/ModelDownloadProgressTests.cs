using FlexyParse.Storage;
using Microsoft.Extensions.Logging;

namespace FlexyParse.Tests;

public sealed class ModelDownloadProgressTests
{
    [Fact]
    public void LogsInitialPeriodicAndFinalProgressWithStructuredProperties()
    {
        var logger = new RecordingLogger<ModelDownloadProgress>();
        var time = new ManualTimeProvider();
        const long total = 8 * 1024 * 1024;
        var progress = new ModelDownloadProgress(logger, "Nemotron INT8", "vision.onnx_data", total, time);
        var initial = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, initial.Level);
        Assert.Equal(0d, initial.Properties["DownloadPercent"]);
        Assert.Equal(8d, initial.Properties["TotalMiB"]);

        time.Advance(TimeSpan.FromSeconds(1));
        progress.Report(total / 4);
        Assert.Single(logger.Entries);
        time.Advance(TimeSpan.FromSeconds(1));
        progress.Report(total / 2);
        Assert.Equal(2, logger.Entries.Count);
        var periodic = logger.Entries.Last();
        Assert.Equal(50d, periodic.Properties["DownloadPercent"]);
        Assert.Equal(4d, periodic.Properties["DownloadedMiB"]);
        Assert.Equal("Nemotron INT8", periodic.Properties["Provider"]);
        Assert.Equal("vision.onnx_data", periodic.Properties["ModelFile"]);

        for (var i = 0; i < 1000; i++) progress.Report(total / 2);
        Assert.Equal(2, logger.Entries.Count);
        progress.Complete(total);
        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(100d, logger.Entries.Last().Properties["DownloadPercent"]);
    }

    [Fact]
    public void UnknownLengthLogsBytesWithoutInventingAPercentage()
    {
        var logger = new RecordingLogger<ModelDownloadProgress>();
        var time = new ManualTimeProvider();
        var progress = new ModelDownloadProgress(logger, "RapidOCR", "model.onnx", null, time);
        time.Advance(TimeSpan.FromSeconds(2));
        progress.Report(1024 * 1024);
        progress.Complete(2 * 1024 * 1024);
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Contains("total size unknown", entry.Message);
            Assert.False(entry.Properties.ContainsKey("DownloadPercent"));
        });
        Assert.Equal(2d, logger.Entries.Last().Properties["DownloadedMiB"]);
    }

    [Fact]
    public async Task BoundedCopyReportsOnlySuccessfullyWrittenBytes()
    {
        using var input = new MemoryStream(new byte[300000]);
        using var output = new MemoryStream();
        var reported = new List<long>();
        await LimitedStreamCopy.CopyAsync(input, output, input.Length, default, count =>
        {
            Assert.Equal(output.Length, count);
            reported.Add(count);
        });
        Assert.Equal(new long[] { 131072, 262144, 300000 }, reported);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += (long)duration.TotalMilliseconds;
    }
}
