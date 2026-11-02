using System.Net;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace FlexyParse.Tests;

public sealed partial class HttpConversionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task KeepResultsPreservesHttpFilesAfterCompletionExpiryAndShutdown(bool background, bool failure)
    {
        var clock = new TaskTestClock();
        var converter = new TaskGateConverter(failure);
        converter.Release.TrySetResult();
        await using var server = await TestServer.StartAsync(services =>
        {
            services.Configure<FlexyParseOptions>(options => options.KeepResults = true);
            services.Configure<FlexyParse.Hosting.AsyncConversionOptions>(options => options.ResultRetentionSeconds = 5);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IDocumentConverter>(converter);
        });
        try
        {
            if (background)
            {
                var id = await SubmitTaskAsync(server.Client);
                await WaitForTaskAsync(server.Client, id, failure ? "failure" : "success");
                clock.Advance(TimeSpan.FromSeconds(6));
                using var expired = await server.Client.GetAsync($"/v1/result/{id}");
                Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
            }
            else
            {
                using var form = Form("# Debug", "debug.md");
                using var response = await server.Client.PostAsync("/v1/convert/file", form);
                Assert.Equal(failure ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
                _ = await response.Content.ReadAsStringAsync();
            }

            var directory = converter.UploadDirectory!;
            Assert.True(Directory.Exists(directory));
            Assert.Contains(Directory.GetFiles(directory), path => Path.GetExtension(path) == ".md");
            Assert.Contains(Directory.GetFiles(directory), path => Path.GetExtension(path) == "");
            if (!failure)
            {
                Assert.True(File.Exists(Path.Combine(directory, "result")));
            }
            await server.StopAsync();
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            await server.StopAsync();
            if (converter.UploadDirectory is { } directory && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
