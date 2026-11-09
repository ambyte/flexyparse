using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using FlexyParse.Cli;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Storage;

namespace FlexyParse.Hosting;

internal sealed record ConversionResult(string Path, string ContentType, string? DownloadName);

/// <summary>Builds the same disk-backed result for synchronous requests and background tasks.</summary>
internal static class ConversionResultBuilder
{
    public static async Task<ConversionResult> BuildAsync(ConversionBatch batch, IDocumentConverter converter,
        FlexyParseOptions settings, Stopwatch stopwatch, CancellationToken token)
    {
        var formats = batch.Options.GetFormats();
        var referencedImages = batch.Options.IncludeImages && batch.Options.GetExportMode() == ImageExportMode.Referenced;
        var imagesDirectory = referencedImages ? Path.Combine(batch.Directory, "images") : null;
        var results = new List<ConvertedFile>();
        await Parallel.ForEachAsync(batch.Files, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(settings.ResolveDocumentParallelism(), batch.Files.Count),
            CancellationToken = token
        }, async (file, ct) =>
        {
            foreach (var format in formats)
            {
                var path = Path.Combine(batch.Directory, Guid.NewGuid().ToString("N") + CliArguments.Extension(format));
                await using var destination = File.Create(path);
                await converter.ConvertAsync(new ConversionInput
                {
                    SourcePath = file.Path,
                    MoveSource = formats.Count == 1,
                    FileName = file.FileName,
                    Format = format,
                    RecognizeImages = batch.Options.DoOcr,
                    OcrLanguage = batch.Options.OcrLanguage,
                    ImageExportMode = batch.Options.GetExportMode(),
                    IncludeImages = batch.Options.IncludeImages,
                    ImageOutputDirectory = imagesDirectory
                }, destination, ct).ConfigureAwait(false);
                lock (results)
                {
                    results.Add(new ConvertedFile(file.FileName, format, path));
                }
            }
        }).ConfigureAwait(false);

        var resultPath = Path.Combine(batch.Directory, "result");
        if (batch.Files.Count == 1 && !referencedImages)
        {
            var document = new Dictionary<string, object?>();
            foreach (var result in results)
            {
                if (result.Format == OutputFormat.Json)
                {
                    await using var input = File.OpenRead(result.Path);
                    using var json = await JsonDocument.ParseAsync(input, cancellationToken: token).ConfigureAwait(false);
                    document["json_content"] = json.RootElement.Clone();
                }
                else
                {
                    document[result.Format == OutputFormat.Markdown ? "md_content" : "html_content"] =
                        await File.ReadAllTextAsync(result.Path, token).ConfigureAwait(false);
                }
            }

            await using var output = File.Create(resultPath);
            await JsonSerializer.SerializeAsync(output, new
            {
                document,
                status = "success",
                processing_time = stopwatch.Elapsed.TotalSeconds,
                timings = new { },
                errors = Array.Empty<object>()
            }, cancellationToken: token).ConfigureAwait(false);
        }
        else
        {
            await using var output = File.Create(resultPath);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in results.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Format))
            {
                token.ThrowIfCancellationRequested();
                var stem = Path.GetFileNameWithoutExtension(FileNameSanitizer.Sanitize(result.FileName));
                var extension = CliArguments.Extension(result.Format);
                var name = stem + extension;
                for (var suffix = 2; !usedNames.Add(name); suffix++)
                {
                    name = $"{stem}-{suffix}{extension}";
                }

                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var input = File.OpenRead(result.Path);
                await input.CopyToAsync(entryStream, token).ConfigureAwait(false);
            }
            if (imagesDirectory is not null && Directory.Exists(imagesDirectory))
            {
                foreach (var image in Directory.EnumerateFiles(imagesDirectory).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry("images/" + Path.GetFileName(image), CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await using var input = File.OpenRead(image);
                    await input.CopyToAsync(entryStream, token).ConfigureAwait(false);
                }
            }
        }

        token.ThrowIfCancellationRequested();
        return batch.Files.Count == 1 && !referencedImages
            ? new ConversionResult(resultPath, "application/json; charset=utf-8", null)
            : new ConversionResult(resultPath, "application/zip", "flexyparse.zip");
    }

    private sealed record ConvertedFile(string FileName, OutputFormat Format, string Path);
}
