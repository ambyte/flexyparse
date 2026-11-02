using FlexyParse.ImageLayout;
using FlexyParse.Options;
using Microsoft.Extensions.Options;

namespace FlexyParse.Hosting;

public sealed class ImageLayoutStartupService(
    ImageLayoutModelStore modelStore, IImageLayoutDetector detector, IOptions<FlexyParseOptions> options,
    ILogger<ImageLayoutStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Value.ImageLayout.Enabled)
        {
            return;
        }

        await modelStore.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await detector.InitializeAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Local document layout model is ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
