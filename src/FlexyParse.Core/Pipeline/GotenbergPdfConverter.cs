using System.Net;
using System.Net.Http.Headers;
using FlexyParse.Options;
using FlexyParse.Pdf;
using FlexyParse.Storage;
using Microsoft.Extensions.Options;

namespace FlexyParse.Pipeline;

public sealed class GotenbergPdfConverter(IHttpClientFactory clientFactory, IOptions<FlexyParseOptions> options)
{
    public const string HttpClientName = "Gotenberg";

    public async Task ConvertAsync(JobWorkspace workspace, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var baseUrl = settings.GotenbergUrl.Trim().TrimEnd('/') + "/";
        var extension = Path.GetExtension(workspace.SourceFileName);
        var isHtml = extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
        var route = isHtml ? "forms/chromium/convert/html" : "forms/libreoffice/convert";
        var convertedPath = Path.Combine(workspace.Root, "converted.pdf");

        // The timeout must also cover the streamed response body, not just the response headers.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.GotenbergTimeoutSeconds));
        try
        {
            // Release the uploaded file before replacing it (required on Windows).
            await DownloadPdfAsync().ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (!PdfSignature.HasPdfHeader(convertedPath))
            {
                throw new DocumentConversionException(ConversionStatus.ServiceUnavailable, "Gotenberg returned an invalid PDF.");
            }

            File.Move(convertedPath, workspace.SourcePath, overwrite: true);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DocumentConversionException(ConversionStatus.ServiceUnavailable, "The timeout for Gotenberg conversion was exceeded.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new DocumentConversionException(ConversionStatus.ServiceUnavailable, "The Gotenberg conversion service is unavailable.", exception);
        }

        async Task DownloadPdfAsync()
        {
            using var client = clientFactory.CreateClient(HttpClientName);
            using var form = new MultipartFormDataContent();
            await using var source = new FileStream(workspace.SourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var content = new StreamContent(source);
            content.Headers.ContentType = new MediaTypeHeaderValue(isHtml ? "text/html" : "application/octet-stream");
            form.Add(content, "files", isHtml ? "index.html" : workspace.SourceFileName);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), route)) { Content = form };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
            request.Headers.Add("Gotenberg-Trace", workspace.JobId);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode switch
                {
                    HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity => ConversionStatus.BadRequest,
                    HttpStatusCode.RequestEntityTooLarge => ConversionStatus.PayloadTooLarge,
                    _ => ConversionStatus.ServiceUnavailable
                };
                throw new DocumentConversionException(status, status == ConversionStatus.BadRequest
                    ? "Gotenberg did not manage to convert the document to PDF. Please check the file format and content."
                    : "Error occurred while converting with Gotenberg.");
            }

            if (response.Content.Headers.ContentLength > settings.MaxUploadBytes)
            {
                throw new DocumentConversionException(ConversionStatus.PayloadTooLarge, "The PDF from Gotenberg exceeds the allowed document size.");
            }

            await using (var pdf = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var destination = new FileStream(convertedPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await LimitedStreamCopy.CopyAsync(pdf, destination, settings.MaxUploadBytes, timeout.Token).ConfigureAwait(false);
            }
        }
    }
}
