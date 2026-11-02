using System.Text.Json;
using System.Text.Json.Serialization;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace FlexyParse.Hosting;

internal sealed class ConversionBatch : IDisposable
{
    private readonly bool _keepResults;
    public string Directory { get; }
    public List<UploadedFile> Files { get; } = [];
    public ConversionOptions Options { get; set; } = new();
    public string? SourceRequestPath { get; set; }

    public ConversionBatch(FlexyParseOptions settings)
    {
        _keepResults = settings.KeepResults;
        Directory = Path.Combine(settings.ResolveTempDirectory(), "upload-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string NewPath() => Path.Combine(Directory, Guid.NewGuid().ToString("N"));

    public static async Task<ConversionBatch> ReadFilesAsync(HttpRequest request, FlexyParseOptions settings, CancellationToken token)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Expected multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).ToString();
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw Invalid("The multipart request has no boundary.");
        }

        var batch = new ConversionBatch(settings);
        try
        {
            var formats = new List<string>();
            bool? doOcr = null;
            string? ocrLanguage = null;
            var reader = new MultipartReader(boundary, request.Body);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(token).ConfigureAwait(false)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    throw Invalid("Invalid multipart field.");
                }

                var field = HeaderUtilities.RemoveQuotes(disposition.Name).ToString();
                if (disposition.IsFileDisposition())
                {
                    if (field != "files")
                    {
                        throw Invalid("Files must be submitted in the 'files' field.");
                    }

                    batch.CheckCount(settings);
                    var name = FileNameSanitizer.Sanitize(HeaderUtilities.RemoveQuotes(
                        disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).ToString());
                    var path = batch.NewPath();
                    await using var output = File.Create(path);
                    await LimitedStreamCopy.CopyAsync(section.Body, output, settings.MaxUploadBytes, token).ConfigureAwait(false);
                    batch.Files.Add(new UploadedFile(name, path));
                }
                else
                {
                    using var buffer = new MemoryStream();
                    await LimitedStreamCopy.CopyAsync(section.Body, buffer, 16 * 1024, token).ConfigureAwait(false);
                    var value = System.Text.Encoding.UTF8.GetString(buffer.ToArray()).Trim();
                    switch (field)
                    {
                        case "to_formats":
                            if (formats.Count >= 3)
                            {
                                throw Invalid("Specify no more than three to_formats.");
                            }

                            formats.Add(value);
                            break;
                        case "do_ocr" when doOcr is null && bool.TryParse(value, out var parsed):
                            doOcr = parsed;
                            break;
                        case "ocr_language" when ocrLanguage is null:
                            ocrLanguage = value;
                            break;
                        default:
                            throw Invalid($"Unsupported or invalid field: {field}.");
                    }
                }
            }

            if (batch.Files.Count == 0)
            {
                throw Invalid("Pass at least one document in the 'files' field.");
            }

            batch.Options = new ConversionOptions
            {
                ToFormats = formats.Count == 0 ? ["md"] : formats.ToArray(),
                DoOcr = doOcr ?? true
            };
            if (ocrLanguage is not null)
            {
                batch.Options.OcrLanguage = ocrLanguage;
            }
            batch.Options.Validate();
            return batch;
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    public void CheckCount(FlexyParseOptions settings)
    {
        if (Files.Count >= settings.MaxFilesPerRequest)
        {
            throw Invalid($"For one request, you can upload no more than {settings.MaxFilesPerRequest} documents.");
        }
    }

    public void Dispose()
    {
        if (!_keepResults)
        {
            DeleteDirectory(Directory);
        }
    }

    internal static void DeleteDirectory(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException) { /* Startup workspace sweep retries cleanup of locked files. */ }
        catch (UnauthorizedAccessException) { /* Startup workspace sweep retries cleanup. */ }
    }

    internal static DocumentConversionException Invalid(string message) => new(ConversionStatus.BadRequest, message);
}

internal sealed class ConversionOptions
{
    private string? _ocrLanguage;

    public string[] ToFormats { get; init; } = ["md"];
    public bool DoOcr { get; init; } = true;
    public string? OcrLanguage
    {
        get => _ocrLanguage;
        set => _ocrLanguage = value ?? throw ConversionBatch.Invalid("ocr_language must be a non-empty supported language string.");
    }

    [JsonIgnore]
    public IReadOnlyList<OutputFormat> Formats => ToFormats.Select(value => value switch
    {
        "md" => OutputFormat.Markdown,
        "json" => OutputFormat.Json,
        "html" => OutputFormat.Html,
        _ => throw ConversionBatch.Invalid("to_formats only accepts md, json, and html.")
    }).Distinct().ToArray();

    public void Validate()
    {
        if (ToFormats is null || ToFormats.Length == 0 || ToFormats.Length > 3)
        {
            throw ConversionBatch.Invalid("Specify from one to three to_formats.");
        }

        _ = Formats;
        if (OcrLanguage is not null)
        {
            try
            {
                OcrLanguage = OcrLanguageProfiles.Normalize(OcrLanguage);
            }
            catch (ArgumentException exception)
            {
                throw ConversionBatch.Invalid(exception.Message);
            }
        }
    }
}

internal readonly record struct UploadedFile(string FileName, string Path);
