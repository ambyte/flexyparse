using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Recognition;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlexyParse.RapidOcr;

/// <summary>Local plain-text OCR using bounded CPU sessions, with no external fallback.</summary>
public sealed class RapidOcrImageRecognizer : IImageRecognizer, IAsyncDisposable
{
    private readonly RecognitionOptions _options;
    private readonly RapidOcrOptions _localOptions;
    private readonly IRapidOcrSessionFactory _factory;
    private readonly RapidOcrModelStore _modelStore;
    private readonly ILogger<RapidOcrImageRecognizer> _logger;
    private readonly ConcurrentDictionary<string, ProfileSession> _profiles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;
    private readonly int _parallelism;
    private readonly ImageRecognitionPipeline? _pipeline;
    private int _disposed;

    public RapidOcrImageRecognizer(IOptions<RecognitionOptions> options, IOptions<RapidOcrOptions> localOptions, IRapidOcrSessionFactory factory,
        ILogger<RapidOcrImageRecognizer> logger, RapidOcrModelStore modelStore, ImageRecognitionPipeline? pipeline = null)
    {
        _options = options.Value;
        _localOptions = localOptions.Value;
        _factory = factory;
        _modelStore = modelStore;
        _pipeline = pipeline;
        _logger = logger;
        _parallelism = _localOptions.MaxConcurrency;
        if (_parallelism is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RapidOCR MaxConcurrency must be 1..16.");
        }

        _gate = new SemaphoreSlim(_parallelism, _parallelism);
    }

    public async Task RecognizeAsync(JobWorkspace workspace, DocumentIndex index, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(index);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var profileName = OcrLanguageProfiles.Normalize(workspace.OcrLanguage ?? _options.Language);
        // Provision before per-image error handling, so network/cache failures fail the whole job clearly.
        var paths = await _modelStore.EnsureAvailableAsync(workspace.OcrLanguage ?? _options.Language, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("RapidOCR provider is not enabled.");
        var profile = new RapidOcrModelProfile { RecognitionModelPath = paths.RecognitionModelPath, DictionaryPath = paths.DictionaryPath };
        var local = _localOptions;
        var sessionOptions = new RapidOcrOptions
        {
            DetectionModelPath = paths.DetectionModelPath, ClassificationModelPath = paths.ClassificationModelPath,
            InferenceThreads = local.InferenceThreads, MaxConcurrency = local.MaxConcurrency,
            MaxImageDimension = local.MaxImageDimension, MaxImagePixels = local.MaxImagePixels
        };

        await Parallel.ForEachAsync(index.Images,
            new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken },
            (image, token) => RecognizeOneAsync(workspace, image, profileName, profile, sessionOptions, token)).ConfigureAwait(false);
    }

    private async ValueTask RecognizeOneAsync(JobWorkspace workspace, ImageManifestEntry image, string profileName,
        RapidOcrModelProfile profile, RapidOcrOptions sessionOptions, CancellationToken cancellationToken)
    {
        var path = workspace.RecognitionPath(image.Hash);
        var temporary = path + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (File.Exists(path))
            {
                return;
            }

            var holder = _profiles.GetOrAdd(profileName, static _ => new ProfileSession());
            string text;
            await holder.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Initialization is CPU/file work too; never block an asynchronous caller on model loading.
                if (holder.Session is null)
                {
                    _logger.LogInformation("Loading RapidOCR models into memory for profile {OcrLanguage}.", profileName);
                    holder.Session = await Task.Run(() => _factory.Create(sessionOptions, profile), cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("RapidOCR models are loaded for profile {OcrLanguage}; recognition is ready.", profileName);
                }
                cancellationToken.ThrowIfCancellationRequested();
                text = _pipeline is null
                    ? await holder.Session.RecognizeAsync(Path.Combine(workspace.Root, image.RelativePath), cancellationToken).ConfigureAwait(false)
                    : await _pipeline.RecognizeFileAsync(workspace, image,
                        (path, mediaType, kind, token) => holder.Session.RecognizeAsync(path, token), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                holder.Gate.Release();
            }

            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JsonDefaults.Options.Encoder });
                WritePlainText(writer, text, cancellationToken);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            File.Delete(workspace.RecognitionErrorPath(image.Hash));
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not ObjectDisposedException)
        {
            _logger.LogWarning(exception, "Local OCR failed for image {ImageHash} in job {JobId}. No external fallback will be used.", image.Hash, workspace.JobId);
            // Native exceptions may contain paths/model details. Keep those out of document output.
            await File.WriteAllTextAsync(workspace.RecognitionErrorPath(image.Hash),
                "Local OCR failed. Verify the local models, dictionary, image limits and native runtime; no external fallback was used.",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static void WritePlainText(Utf8JsonWriter writer, string text, CancellationToken cancellationToken)
    {
        writer.WriteStartArray();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Existing writers store inline Markdown in paragraph text. Escape before entering that contract;
            // never pass OCR output through a Markdown parser (headings, links, HTML and commands stay literal).
            var escaped = new StringBuilder(line.Length);
            foreach (var character in line.Trim())
            {
                if (character == '&')
                {
                    escaped.Append("&amp;");
                }
                else if (character == '<')
                {
                    escaped.Append("&lt;");
                }
                else if (character == '>')
                {
                    escaped.Append("&gt;");
                }
                else
                {
                    if (character is >= '!' and <= '/' or >= ':' and <= '@'
                        or >= '[' and <= '`' or >= '{' and <= '~')
                    {
                        escaped.Append('\\');
                    }

                    escaped.Append(character);
                }
            }

            writer.WriteStartObject();
            writer.WriteString("type", "paragraph");
            writer.WriteString("text", escaped.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Drain in-flight native work before disposing sessions. Waiters recheck the disposed flag.
        for (var i = 0; i < _parallelism; i++)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            foreach (var holder in _profiles.Values)
            {
                holder.Session?.Dispose();
                holder.Gate.Dispose();
            }
        }
        finally
        {
            // Keep the gate alive for callers that were queued when disposal began.
            _gate.Release(_parallelism);
        }
    }

    private sealed class ProfileSession
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IRapidOcrSession? Session { get; set; }
    }
}
