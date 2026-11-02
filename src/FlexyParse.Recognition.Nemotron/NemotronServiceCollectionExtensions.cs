using FlexyParse.Options;
using FlexyParse.Recognition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlexyParse.Nemotron;

public static class NemotronServiceCollectionExtensions
{
    /// <summary>Installs Nemotron recognition after AddFlexyParse. No external recognition fallback.</summary>
    public static IServiceCollection AddNemotronRecognition(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<NemotronOptions>().Bind(configuration.GetSection(NemotronOptions.SectionName))
            // Provider selection is authoritative, including over obsolete explicit ExecutionMode settings.
            .PostConfigure(options => options.ExecutionMode =
                configuration.GetValue<RecognitionProvider>("FlexyParse:Recognition:Provider") == RecognitionProvider.NemotronGPU
                    ? NemotronExecutionMode.WebGpuFp16 : NemotronExecutionMode.CpuInt8)
            .Validate(options => options.IsValid(), "Recognition:Nemotron has invalid execution mode, GPU device, paths, thread, repetition detection, download or image limits.")
            .ValidateOnStart();
        services.AddHttpClient(NemotronModelStore.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FlexyParse/1.0");
        });
        services.TryAddSingleton<NemotronModelStore>();
        services.TryAddSingleton<INemotronSessionFactory, NemotronSessionFactory>();
        services.Replace(ServiceDescriptor.Singleton<IImageRecognizer, NemotronImageRecognizer>());
        services.AddHostedService<NemotronStartupService>();
        return services;
    }
}

public sealed class NemotronStartupService(NemotronModelStore store, ILogger<NemotronStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Selected local Nemotron-3.5-Parse ONNX pack is ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
