using System.Diagnostics;
using FlexyParse.Extraction;
using FlexyParse.Options;
using FlexyParse.Output;
using FlexyParse.Pdf;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlexyParse.Pipeline;

public sealed class DocumentConversionService : IDocumentConverter
{
    private readonly FlexyParseOptions _options;
    private readonly DocumentProcessingGate _gate;
    private readonly DocumentExtractor _extractor;
    private readonly IImageRecognizer _recognizer;
    private readonly MarkdownDocumentWriter _markdownWriter;
    private readonly HtmlDocumentWriter _htmlWriter;
    private readonly JsonDocumentWriter _jsonWriter;
    private readonly ILogger<DocumentConversionService> _logger;
    private readonly GotenbergPdfConverter _pdfConverter;
    private readonly ImageDocumentExtractor _imageExtractor;

    public DocumentConversionService(
        IOptions<FlexyParseOptions> options,
        DocumentProcessingGate gate,
        DocumentExtractor extractor,
        IImageRecognizer recognizer,
        MarkdownDocumentWriter markdownWriter,
        JsonDocumentWriter jsonWriter,
        HtmlDocumentWriter htmlWriter,
        ILogger<DocumentConversionService> logger,
        GotenbergPdfConverter pdfConverter,
        ImageDocumentExtractor? imageExtractor = null)
    {
        _options = options.Value;
        _gate = gate;
        _extractor = extractor;
        _recognizer = recognizer;
        _markdownWriter = markdownWriter;
        _jsonWriter = jsonWriter;
        _htmlWriter = htmlWriter;
        _logger = logger;
        _pdfConverter = pdfConverter;
        _imageExtractor = imageExtractor ?? new ImageDocumentExtractor(options);
    }

    public async Task ConvertAsync(ConversionInput input, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Content is null && string.IsNullOrWhiteSpace(input.SourcePath))
        {
            throw new DocumentConversionException(ConversionStatus.BadRequest, "The document has not been submitted.");
        }

        await using var lease = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        var workspace = JobWorkspace.Create(_options.ResolveTempDirectory(), input.FileName, _options.KeepResults);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            workspace.OcrLanguage = input.OcrLanguage ?? _options.Recognition.Language;
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(input.SourcePath))
            {
                if (input.MoveSource)
                {
                    workspace.MoveSourceFrom(input.SourcePath);
                }
                else
                {
                    await using var source = new FileStream(input.SourcePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await workspace.SaveSourceAsync(source, _options.MaxUploadBytes, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await workspace.SaveSourceAsync(input.Content!, _options.MaxUploadBytes, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(workspace.SourcePath).Length > _options.MaxUploadBytes)
            {
                throw new DocumentConversionException(ConversionStatus.PayloadTooLarge, "The document size exceeds the allowed limit.");
            }

            var extension = Path.GetExtension(input.FileName);
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            {
                if (input.Format == OutputFormat.Markdown)
                {
                    await using var source = new FileStream(workspace.SourcePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }
                else if (input.Format == OutputFormat.Html)
                {
                    await _htmlWriter.WriteMarkdownAsync(workspace, destination, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _jsonWriter.WriteMarkdownAsync(workspace, destination, cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Converted Markdown {File} to {Format} in {ElapsedMs} ms. Job: {JobId}",
                    workspace.SourceFileName, input.Format, stopwatch.ElapsedMilliseconds, workspace.JobId);
                return;
            }

            var isPdf = PdfSignature.HasPdfHeader(workspace.SourcePath);
            var index = isPdf ? null : await _imageExtractor.TryExtractAsync(workspace, cancellationToken).ConfigureAwait(false);
            if (index is null && !isPdf && !extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_options.GotenbergUrl))
            {
                _logger.LogInformation("Converting document {File} to PDF with Gotenberg. Job: {JobId}",
                    workspace.SourceFileName, workspace.JobId);
                await _pdfConverter.ConvertAsync(workspace, cancellationToken).ConfigureAwait(false);
            }
            else if (index is null && !isPdf)
            {
                throw new DocumentConversionException(ConversionStatus.BadRequest, "The file is not a PDF or supported raster image. For Markdown, use the .md or .markdown extension; for other formats, configure Gotenberg.");
            }

            index ??= await _extractor.ExtractAsync(workspace, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var recognize = input.RecognizeImages ?? _options.RecognizeImages;
            if (recognize && index.Images.Count > 0)
            {
                if (_options.Recognition.Provider == RecognitionProvider.OpenAi
                    && _options.ModelApi.RequireApiKey && string.IsNullOrWhiteSpace(_options.ModelApi.ApiKey))
                {
                    throw new DocumentConversionException(
                        ConversionStatus.ServiceUnavailable,
                        "Image recognition is enabled, but no model API key is specified. Please set FLEXYPARSE_MODEL_API_KEY, MODEL_API_KEY or FlexyParse:ModelApi:ApiKey.");
                }

                await _recognizer.RecognizeAsync(workspace, index, cancellationToken).ConfigureAwait(false);
            }

            if (input.Format == OutputFormat.Markdown)
            {
                await _markdownWriter.WriteAsync(workspace, index, destination, cancellationToken).ConfigureAwait(false);
            }
            else if (input.Format == OutputFormat.Html)
            {
                await _htmlWriter.WriteAsync(workspace, index, destination, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _jsonWriter.WriteAsync(workspace, index, destination, cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Converted {File} to {Format} in {ElapsedMs} ms. Pages: {Pages}. Images: {Images}. Job: {JobId}",
                workspace.SourceFileName,
                input.Format,
                stopwatch.ElapsedMilliseconds,
                index.Metadata.PageCount,
                index.Images.Count,
                workspace.JobId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Conversion cancelled for {File} after {ElapsedMs} ms. Job: {JobId}",
                workspace.SourceFileName, stopwatch.ElapsedMilliseconds, workspace.JobId);
            throw;
        }
        finally
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
            if (_options.KeepResults)
            {
                _logger.LogInformation("Preserved conversion workspace at {Directory}", workspace.Root);
            }
        }
    }
}
