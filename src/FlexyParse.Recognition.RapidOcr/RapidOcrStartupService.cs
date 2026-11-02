using FlexyParse.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlexyParse.RapidOcr;

public sealed class RapidOcrStartupService(
    RapidOcrModelStore modelStore, IOptions<RecognitionOptions> options,
    ILogger<RapidOcrStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var language = options.Value.Language;
        var paths = await modelStore.EnsureAvailableAsync(language, cancellationToken).ConfigureAwait(false);
        if (paths is not null)
        {
            logger.LogInformation("Local RapidOCR models are ready for default language {OcrLanguage}.", language);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
