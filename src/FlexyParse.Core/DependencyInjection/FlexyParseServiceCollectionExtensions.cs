using FlexyParse.Extraction;
using FlexyParse.ImageLayout;
using FlexyParse.Markdown;
using FlexyParse.Options;
using FlexyParse.Output;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FlexyParse.DependencyInjection;

public static class FlexyParseServiceCollectionExtensions
{
    public static IServiceCollection AddFlexyParse(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<FlexyParseOptions>, RecognitionOptionsValidator>();
        services.AddOptions<RecognitionOptions>().Configure<IOptions<FlexyParseOptions>>((options, root) =>
        {
            options.Provider = root.Value.Recognition.Provider;
            options.Language = root.Value.Recognition.Language;
        });

        services.AddOptions<FlexyParseOptions>()
            .Bind(configuration.GetSection(FlexyParseOptions.SectionName))
            .Validate(options => options.MaxUploadBytes > 0, "MaxUploadBytes must be positive.")
            .Validate(options => options.MaxFilesPerRequest > 0, "MaxFilesPerRequest must be positive.")
            .Validate(options => string.IsNullOrWhiteSpace(options.GotenbergUrl)
                || (Uri.TryCreate(options.GotenbergUrl.Trim(), UriKind.Absolute, out var address)
                    && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
                    && address.UserInfo.Length == 0 && address.Query.Length == 0 && address.Fragment.Length == 0),
                "GotenbergUrl must be an HTTP(S) base URL without credentials, query or fragment.")
            .Validate(options => options.GotenbergTimeoutSeconds > 0, "GotenbergTimeoutSeconds must be positive.")
            .Validate(options => options.ImageLayout.IsValid(), "ImageLayout requires a model path, valid thresholds, resize mode and resource limits.")
            .Validate(options => options.ImageInput.MaxDimension is > 0 and <= 65536
                && options.ImageInput.MaxPixels is > 0 and <= 100_000_000,
                "ImageInput requires MaxDimension between 1 and 65536 and MaxPixels between 1 and 100000000.")
            .ValidateOnStart();
        services.AddHttpClient(GotenbergPdfConverter.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient(OpenAiImageRecognizer.HttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FlexyParseOptions>>().Value.ModelApi;
            client.BaseAddress = options.ResolveBaseAddress();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds));
        });
        services.AddHttpClient(ImageLayoutModelStore.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddSingleton<PdfTextExtractor>();
        services.AddSingleton<PdfImageFallbackRenderer>();
        services.AddSingleton<PdfImageExtractor>();
        services.AddSingleton<PdfPageLayoutAnalyzer>();
        services.AddSingleton<TabulaTableExtractor>();
        services.AddSingleton<DocumentExtractor>();
        services.AddSingleton<ImageDocumentExtractor>();
        services.AddSingleton<IImageLayoutDetector, YoloImageLayoutDetector>();
        services.AddSingleton<ImageLayoutModelStore>();
        services.AddSingleton<ImageLayoutProcessor>();
        services.AddSingleton<ImageRecognitionPipeline>();
        services.TryAddSingleton<IImageRecognizer>(provider =>
        {
            if (provider.GetRequiredService<IOptions<RecognitionOptions>>().Value.Provider != RecognitionProvider.OpenAi)
            {
                throw new InvalidOperationException("The selected recognition provider is not registered. Register its module after AddFlexyParse; external fallback is disabled.");
            }

            return ActivatorUtilities.CreateInstance<OpenAiImageRecognizer>(provider);
        });
        services.AddSingleton<MarkdownDocumentWriter>();
        services.AddSingleton<HtmlDocumentWriter>();
        services.AddSingleton<MarkdownJsonParser>();
        services.AddSingleton<MarkdownJsonFormatter>();
        services.AddSingleton<JsonDocumentWriter>();
        services.AddSingleton<DocumentProcessingGate>();
        services.AddSingleton<GotenbergPdfConverter>();
        services.AddSingleton<IDocumentConverter, DocumentConversionService>();
        return services;
    }
}
