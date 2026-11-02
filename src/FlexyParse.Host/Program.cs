using System.Text;
using FlexyParse.Cli;
using FlexyParse.Hosting;
using FlexyParse.Options;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace FlexyParse.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "serve" or "--serve")
        {
            var webArgs = args.Where(static argument => argument is not "serve" and not "--serve").ToArray();
            await RunWebAsync(webArgs).ConfigureAwait(false);
            return 0;
        }

        if (args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(CliArguments.Help);
            return 0;
        }

        return await RunCliAsync(args).ConfigureAwait(false);
    }

    private static async Task RunWebAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ApplyConfiguration(builder.Configuration);
        builder.Services.AddConfiguredFlexyParse(builder.Configuration);
        builder.Services.AddHostedService<ImageLayoutStartupService>();
        SourceDocumentService.AddServices(builder.Services);
        ConversionTaskService.AddServices(builder.Services);
        ApiKeyAuthentication.AddServices(builder.Services, builder.Configuration);
        ApiDocumentation.AddServices(builder.Services);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = null;
        });

        var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<FlexyParseOptions>>().Value;
        Directory.CreateDirectory(options.ResolveTempDirectory());
        JobWorkspace.Sweep(options, app.Logger);
        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler(handler => handler.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Internal service error.",
                    status = StatusCodes.Status500InternalServerError
                }).ConfigureAwait(false);
            }));
        }

        ApiKeyAuthentication.Use(app);
        app.UseWebSockets();
        ApiDocumentation.Use(app);
        app.UseDefaultFiles();
        app.UseStaticFiles();
        ConvertEndpoints.Map(app);
        await app.RunAsync().ConfigureAwait(false);
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        ApplyConfiguration(builder.Configuration);
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddConfiguredFlexyParse(builder.Configuration);
        builder.Services.AddHostedService<ImageLayoutStartupService>();
        builder.Services.AddSingleton<CliRunner>();

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<FlexyParseOptions>>().Value;
        Directory.CreateDirectory(options.ResolveTempDirectory());
        JobWorkspace.Sweep(options, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FlexyParse"));
        await host.StartAsync().ConfigureAwait(false);
        try
        {
            return await host.Services.GetRequiredService<CliRunner>().RunAsync(args,
                host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping).ConfigureAwait(false);
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
        }
    }

    private static void ApplyConfiguration(ConfigurationManager configuration)
    {
        HostConfiguration.Apply(configuration);
    }
}
