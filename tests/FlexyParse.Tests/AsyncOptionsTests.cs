using System.Text;
using FlexyParse.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlexyParse.Tests;

public sealed class AsyncOptionsTests
{
    private const string JsonDefaults = """
        {
          "FlexyParse": {
            "Async": {
              "WorkerCount": 2,
              "QueueCapacity": 32,
              "MaxStoredTasks": 128,
              "ResultRetentionSeconds": 3600,
              "CleanupIntervalSeconds": 60,
              "MaxWebSocketConnections": 64
            }
          }
        }
        """;

    [Fact]
    public void EnvironmentOverridesAllSixJsonSettings()
    {
        var prefix = "FlexyParseAsyncOptions_" + Guid.NewGuid().ToString("N") + "_";
        var overrides = new Dictionary<string, int>
        {
            ["WorkerCount"] = 3,
            ["QueueCapacity"] = 48,
            ["MaxStoredTasks"] = 200,
            ["ResultRetentionSeconds"] = 1800,
            ["CleanupIntervalSeconds"] = 30,
            ["MaxWebSocketConnections"] = 96
        };
        try
        {
            foreach (var (name, value) in overrides)
            {
                Environment.SetEnvironmentVariable(prefix + "FlexyParse__Async__" + name,
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            using var json = new MemoryStream(Encoding.UTF8.GetBytes(JsonDefaults));
            using var configuration = new ConfigurationManager();
            configuration.AddJsonStream(json);
            configuration.AddEnvironmentVariables(prefix);
            using var provider = CreateProvider(configuration);
            var options = provider.GetRequiredService<IOptions<AsyncConversionOptions>>().Value;
            Assert.Equal(3, options.WorkerCount);
            Assert.Equal(48, options.QueueCapacity);
            Assert.Equal(200, options.MaxStoredTasks);
            Assert.Equal(1800, options.ResultRetentionSeconds);
            Assert.Equal(30, options.CleanupIntervalSeconds);
            Assert.Equal(96, options.MaxWebSocketConnections);
        }
        finally
        {
            foreach (var name in overrides.Keys)
            {
                Environment.SetEnvironmentVariable(prefix + "FlexyParse__Async__" + name, null);
            }
        }
    }

    [Fact]
    public void JsonDefaultsAreUsedWithoutEnvironmentOverrides()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(JsonDefaults));
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(json);
        using var provider = CreateProvider(configuration);
        var options = provider.GetRequiredService<IOptions<AsyncConversionOptions>>().Value;
        Assert.Equal(2, options.WorkerCount);
        Assert.Equal(32, options.QueueCapacity);
        Assert.Equal(128, options.MaxStoredTasks);
        Assert.Equal(3600, options.ResultRetentionSeconds);
        Assert.Equal(60, options.CleanupIntervalSeconds);
        Assert.Equal(64, options.MaxWebSocketConnections);
    }

    private static ServiceProvider CreateProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        ConversionTaskService.AddServices(services);
        return services.BuildServiceProvider();
    }
}
