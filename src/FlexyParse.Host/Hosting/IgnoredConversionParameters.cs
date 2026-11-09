using System.Text.Json;

namespace FlexyParse.Hosting;

/// <summary>Logs unsupported field names only, never their potentially sensitive values.</summary>
internal static class IgnoredConversionParameters
{
    public static void Log(HttpRequest request, string name)
    {
        // Bound untrusted names and escape control characters to prevent forged multiline log entries.
        var safeName = JsonSerializer.Serialize(name.Length > 256 ? name[..256] + "..." : name);
        var logger = request.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("FlexyParse.Hosting.ConversionParameters");
        logger.LogWarning("Unsupported conversion parameter {ParameterName} was ignored.", safeName);
    }

    public static void LogAndClear(HttpRequest request, Dictionary<string, JsonElement>? parameters, string prefix = "")
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var name in parameters.Keys)
        {
            Log(request, prefix + name);
        }
        // In particular, do not retain unknown JSON values on queued conversion options.
        parameters.Clear();
    }
}
