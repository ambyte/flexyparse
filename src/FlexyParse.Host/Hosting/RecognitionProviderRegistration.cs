using FlexyParse.DependencyInjection;
using FlexyParse.Options;
using FlexyParse.RapidOcr;
using FlexyParse.Nemotron;
using Microsoft.Extensions.Options;

namespace FlexyParse.Hosting;

/// <summary>Composition root for optional recognition providers; Core never references implementations.</summary>
internal static class RecognitionProviderRegistration
{
    public static IServiceCollection AddConfiguredFlexyParse(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddFlexyParse(configuration);
        var provider = configuration.GetValue<RecognitionProvider>("FlexyParse:Recognition:Provider");
        switch (provider)
        {
            case RecognitionProvider.OpenAi:
                break;
            case RecognitionProvider.RapidOcr:
                services.AddRapidOcrRecognition(configuration);
                break;
            case RecognitionProvider.NemotronCPU:
            case RecognitionProvider.NemotronGPU:
                services.AddNemotronRecognition(configuration);
                break;
            default:
                throw new OptionsValidationException(nameof(FlexyParseOptions), typeof(FlexyParseOptions),
                    ["Recognition:Provider must be OpenAi, RapidOcr, NemotronCPU or NemotronGPU."]);
        }

        return services;
    }
}
