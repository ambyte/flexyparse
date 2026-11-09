using FlexyParse.Options;
using Microsoft.Extensions.Logging;

namespace FlexyParse.Storage;

public sealed class JobWorkspace : IAsyncDisposable
{
    private static readonly TimeSpan StaleJobAge = TimeSpan.FromHours(24);
    private readonly bool _keepResults;
    private int _disposed;

    private JobWorkspace(string root, string sourceFileName, bool keepResults)
    {
        _keepResults = keepResults;
        Root = root;
        JobId = Path.GetFileName(root);
        SourceFileName = sourceFileName;
        SourcePath = Path.Combine(root, "source.pdf");
        PagesDirectory = Path.Combine(root, "pages");
        ImagesDirectory = Path.Combine(root, "images");
    }

    public string Root { get; }

    public string JobId { get; }

    public string? OcrLanguage { get; set; }

    public Pipeline.ImageExportMode? ImageExportMode { get; set; }

    public IReadOnlyDictionary<string, string?> ExportedImageSources { get; set; } = new Dictionary<string, string?>();

    public string SourceFileName { get; }

    public string SourcePath { get; }

    public string PagesDirectory { get; }

    public string ImagesDirectory { get; }

    public string ManifestPath => Path.Combine(ImagesDirectory, "manifest.json");

    public static JobWorkspace Create(string tempRoot, string sourceFileName, bool keepResults = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        Directory.CreateDirectory(tempRoot);
        var root = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "pages"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        return new JobWorkspace(root, FileNameSanitizer.Sanitize(sourceFileName), keepResults);
    }

    public string PagePath(int pageNumber) => Path.Combine(PagesDirectory, $"{pageNumber:D6}.json");

    public string RecognitionPath(string hash) => Path.Combine(ImagesDirectory, hash + ".recognition.json");

    public string RecognitionErrorPath(string hash) => Path.Combine(ImagesDirectory, hash + ".error.txt");

    public async Task SaveSourceAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var destination = new FileStream(
            SourcePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await LimitedStreamCopy.CopyAsync(content, destination, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    public void MoveSourceFrom(string path)
    {
        if (File.Exists(SourcePath))
        {
            File.Delete(SourcePath);
        }

        File.Move(path, SourcePath);
    }

    public static void Sweep(FlexyParseOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (options.KeepResults)
        {
            logger.LogWarning("Debug result retention is enabled. Temporary conversion files at {Directory} require manual cleanup.",
                options.ResolveTempDirectory());
            return;
        }

        var root = options.ResolveTempDirectory();
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleJobAge;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                var newest = info.LastWriteTimeUtc;
                foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (file.LastWriteTimeUtc > newest)
                    {
                        newest = file.LastWriteTimeUtc;
                    }
                }

                if (newest > cutoff)
                {
                    continue;
                }

                info.Delete(recursive: true);
                logger.LogInformation("Deleted stale temporary directory {Directory}", info.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to delete stale temporary directory {Directory}", directory);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _keepResults)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // The next startup sweep removes leftovers that are still locked.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
