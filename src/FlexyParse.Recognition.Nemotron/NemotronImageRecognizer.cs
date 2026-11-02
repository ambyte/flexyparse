using System.Text.Json;
using FlexyParse.Markdown;
using FlexyParse.Model;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlexyParse.Nemotron;

/// <summary>Serialized local CPU/WebGPU recognition with a shared model instance and no remote fallback.</summary>
public sealed class NemotronImageRecognizer(NemotronModelStore store, INemotronSessionFactory factory,
    IOptions<NemotronOptions> options, MarkdownJsonParser parser, ILogger<NemotronImageRecognizer> logger,
    ImageRecognitionPipeline? pipeline = null)
    : IImageRecognizer, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private INemotronSession? _session;
    private int _disposed;

    public async Task RecognizeAsync(JobWorkspace workspace, DocumentIndex index, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(index);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        // Model/cache errors fail the job rather than being converted to an image-level error.
        var directory = await store.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        foreach (var image in index.Images)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var path = workspace.RecognitionPath(image.Hash);
                if (File.Exists(path)) continue;
                // Load failures fail clearly; never retry initialization for every image in a damaged pack.
                if (_session is null)
                {
                    if (options.Value.ExecutionMode == NemotronExecutionMode.CpuInt8)
                    {
                        logger.LogInformation("Loading Nemotron model graphs for {ExecutionMode} (CPU).", options.Value.ExecutionMode);
                    }
                    else
                    {
                        logger.LogInformation("Loading Nemotron model graphs for {ExecutionMode} (GPU device {GpuDeviceId}).", options.Value.ExecutionMode, options.Value.GpuDeviceId);
                    }
                    _session = await Task.Run(() => factory.Create(directory, options.Value), cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Nemotron model graphs are loaded for {ExecutionMode}; recognition is ready.", options.Value.ExecutionMode);
                }
                await RecognizeOneAsync(workspace, image, path, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
    }

    private async Task RecognizeOneAsync(JobWorkspace workspace, ImageManifestEntry image, string path, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            async Task<string> RecognizeRegionAsync(string imagePath, string mediaType, string? kind, CancellationToken regionToken)
            {
                return await NemotronRepetitionRecovery.RecognizeAsync(imagePath, options.Value,
                    async (source, sourceToken) =>
                    {
                        var content = await _session!.RecognizeAsync(source, sourceToken).ConfigureAwait(false);
                        return NemotronOutputNormalizer.Normalize(content, sourceToken);
                    }, logger, regionToken).ConfigureAwait(false);
            }
            var markdown = pipeline is null
                ? await RecognizeRegionAsync(Path.Combine(workspace.Root, image.RelativePath), image.MediaType, null, token).ConfigureAwait(false)
                : await pipeline.RecognizeFileAsync(workspace, image, RecognizeRegionAsync, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await using var writer = new Utf8JsonWriter(stream);
                parser.Write(writer, markdown, token);
                await writer.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            File.Delete(workspace.RecognitionErrorPath(image.Hash));
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not ObjectDisposedException)
        {
            logger.LogWarning(exception, "Nemotron recognition failed for image {ImageHash} in job {JobId}; no external fallback.",
                image.Hash, workspace.JobId);
            await File.WriteAllTextAsync(workspace.RecognitionErrorPath(image.Hash),
                "Local Nemotron recognition failed. Check image limits, repetitive output, model context and native runtime; no external fallback was used.", token)
                .ConfigureAwait(false);
        }
        finally { File.Delete(temporary); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _session?.Dispose(); }
        // Queued callers recheck the disposed flag, so leave the semaphore usable for them.
        finally { _gate.Release(); }
    }
}
