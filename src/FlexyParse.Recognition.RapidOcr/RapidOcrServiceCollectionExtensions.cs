using FlexyParse.Recognition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FlexyParse.RapidOcr;

public static class RapidOcrServiceCollectionExtensions
{
    /// <summary>Installs the local provider and its startup provisioning after AddFlexyParse.</summary>
    public static IServiceCollection AddRapidOcrRecognition(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RapidOcrOptions>, RapidOcrOptionsValidator>());
        services.AddOptions<RapidOcrOptions>()
            .Bind(configuration.GetSection(RapidOcrOptions.SectionName))
            .ValidateOnStart();
        services.AddHttpClient(RapidOcrModelStore.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FlexyParse/1.0");
        });
        services.TryAddSingleton<IRapidOcrSessionFactory, RapidOcrSessionFactory>();
        services.TryAddSingleton<RapidOcrModelStore>();
        services.Replace(ServiceDescriptor.Singleton<IImageRecognizer, RapidOcrImageRecognizer>());
        services.AddHostedService<RapidOcrStartupService>();
        return services;
    }
}
