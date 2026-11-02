using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FlexyParse.Hosting;

public sealed class ApiKeyOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

public static class ApiKeyAuthentication
{
    public const string HeaderName = "X-Api-Key";

    public static void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiKeyOptions>()
            .Bind(configuration.GetSection("FlexyParse:Host"))
            .Validate(options => options.ApiKey.All(character => character is >= '!' and <= '~'),
                "FlexyParse:Host:ApiKey must be empty or contain only printable ASCII characters without spaces.")
            .ValidateOnStart();
    }

    public static void Use(WebApplication app)
    {
        var key = app.Services.GetRequiredService<IOptions<ApiKeyOptions>>().Value.ApiKey;
        if (key.Length == 0)
        {
            return;
        }

        // Compare fixed-length digests so key contents and length do not affect comparison timing.
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/v1"))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.Headers.TryGetValue(HeaderName, out var values)
                && values.Count == 1
                && !string.IsNullOrEmpty(values[0])
                && CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!))))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            await Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized.", detail: "A valid X-Api-Key header is required.")
                .ExecuteAsync(context).ConfigureAwait(false);
        });
    }
}
