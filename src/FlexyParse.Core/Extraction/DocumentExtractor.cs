using System.Text.Json;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace FlexyParse.Extraction;

/// <summary>
/// Reads one page at a time and writes it to the job directory before moving on,
/// so a large PDF does not have to stay in memory as a single document model.
/// </summary>
public sealed class DocumentExtractor
{
    private readonly FlexyParseOptions _options;
    private readonly PdfTextExtractor _textExtractor;
    private readonly PdfImageExtractor _imageExtractor;
    private readonly TabulaTableExtractor _tableExtractor;
    private readonly ILogger<DocumentExtractor> _logger;
    private readonly PdfPageLayoutAnalyzer? _layoutAnalyzer;

    public DocumentExtractor(
        IOptions<FlexyParseOptions> options,
        PdfTextExtractor textExtractor,
        PdfImageExtractor imageExtractor,
        TabulaTableExtractor tableExtractor,
        ILogger<DocumentExtractor> logger,
        PdfPageLayoutAnalyzer? layoutAnalyzer = null)
    {
        _options = options.Value;
        _textExtractor = textExtractor;
        _imageExtractor = imageExtractor;
        _tableExtractor = tableExtractor;
        _logger = logger;
        _layoutAnalyzer = layoutAnalyzer;
    }

    public Task<DocumentIndex> ExtractAsync(JobWorkspace workspace, CancellationToken cancellationToken)
    {
        return Task.Run(() => ExtractCoreAsync(workspace, cancellationToken), cancellationToken);
    }

    private async Task<DocumentIndex> ExtractCoreAsync(JobWorkspace workspace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsing = new ParsingOptions
        {
            ClipPaths = true,
            UseLenientParsing = true,
            SkipMissingFonts = true
        };

        using var stream = new FileStream(
            workspace.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.RandomAccess);

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(stream, parsing);
        }
        catch (Exception exception) when (IsPdfPigException(exception))
        {
            throw new DocumentConversionException(ConversionStatus.BadRequest, "The PDF could not be opened. The file is corrupted or encrypted.", exception);
        }

        using (document)
        {
            var images = new Dictionary<string, ImageManifestEntry>(StringComparer.Ordinal);
            var pageCount = document.NumberOfPages;
            for (var number = 1; number <= pageCount; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation(
                    "Processing page {Page}/{PageCount} of document {File} in job {JobId}",
                    number,
                    pageCount,
                    workspace.SourceFileName,
                    workspace.JobId);
                var stored = await ExtractPageAsync(document, workspace, images, number, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                using var pageStream = new FileStream(
                    workspace.PagePath(number),
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.SequentialScan);
                JsonSerializer.Serialize(pageStream, stored, JsonDefaults.Options);

                if (number % 25 == 0 || number == pageCount)
                {
                    _logger.LogInformation(
                        "Extracted {Page}/{PageCount} pages from {File} in job {JobId}",
                        number,
                        pageCount,
                        workspace.SourceFileName,
                        workspace.JobId);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var manifest = images.Values.ToList();
            using (var manifestStream = new FileStream(workspace.ManifestPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(manifestStream, manifest, JsonDefaults.Options);
            }

            return new DocumentIndex
            {
                SourceFileName = workspace.SourceFileName,
                Metadata = ReadMetadata(document, pageCount),
                Images = manifest
            };
        }
    }

    private async ValueTask<StoredPage> ExtractPageAsync(
        PdfDocument document,
        JobWorkspace workspace,
        Dictionary<string, ImageManifestEntry> images,
        int number,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.GetPage(number);
            var layout = _layoutAnalyzer is null || page.Letters.Count == 0 ? null
                : await _layoutAnalyzer.AnalyzeAsync(workspace.SourcePath, number, page.Width, page.Height, cancellationToken,
                    _options.KeepResults ? Path.GetDirectoryName(workspace.SourcePath) : null).ConfigureAwait(false);
            var tables = _tableExtractor.ExtractWithLayout(document, number, layout, cancellationToken);
            var text = _textExtractor.Extract(page, tables.Select(table => table.Box).ToArray(), layout, cancellationToken);
            var pictures = _imageExtractor.Extract(page, workspace, images, _options.ModelApi.MaxImageBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new StoredPage
            {
                Number = number,
                Width = page.Width,
                Height = page.Height,
                Blocks = PageContentComposer.Compose(text, tables, pictures, textExcludesTables: true)
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogError(exception, "Failed to extract page {Page} of {File} in job {JobId}", number, workspace.SourceFileName, workspace.JobId);
            return new StoredPage
            {
                Number = number,
                Blocks = [new ErrorBlock { Message = "The page could not be parsed." }]
            };
        }
    }

    private static DocumentMetadata ReadMetadata(PdfDocument document, int pageCount)
    {
        var information = document.Information;
        return new DocumentMetadata
        {
            Title = EmptyToNull(information.Title),
            Author = EmptyToNull(information.Author),
            Subject = EmptyToNull(information.Subject),
            Keywords = EmptyToNull(information.Keywords),
            Creator = EmptyToNull(information.Creator),
            Producer = EmptyToNull(information.Producer),
            CreationDate = EmptyToNull(information.CreationDate),
            ModificationDate = EmptyToNull(information.ModifiedDate),
            PageCount = pageCount
        };
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsPdfPigException(Exception exception)
    {
        var ns = exception.GetType().Namespace;
        return ns is not null && ns.StartsWith("UglyToad.PdfPig", StringComparison.Ordinal);
    }
}
