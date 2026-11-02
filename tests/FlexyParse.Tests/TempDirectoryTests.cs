using FlexyParse.DependencyInjection;
using FlexyParse.Options;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlexyParse.Tests;

public class TempDirectoryTests
{
    [Fact]
    public async Task KeepResultsPreservesWorkspaceOnDisposalAndStartupSweep()
    {
        var options = new FlexyParseOptions { KeepResults = true };
        var workspace = JobWorkspace.Create(options.ResolveTempDirectory(), "debug.pdf", options.KeepResults);
        try
        {
            await File.WriteAllTextAsync(workspace.SourcePath, "debug input");
            var stale = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(workspace.SourcePath, stale);
            foreach (var directory in Directory.EnumerateDirectories(workspace.Root))
            {
                Directory.SetLastWriteTimeUtc(directory, stale);
            }
            Directory.SetLastWriteTimeUtc(workspace.Root, stale);

            await workspace.DisposeAsync();
            await workspace.DisposeAsync();
            JobWorkspace.Sweep(options, NullLogger.Instance);

            Assert.Equal("debug input", await File.ReadAllTextAsync(workspace.SourcePath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public void UsesSystemTempDirectory()
    {
        var options = new FlexyParseOptions();

        Assert.Equal(Path.Combine(Path.GetTempPath(), "flexyparse"), options.ResolveTempDirectory());
    }

    [Fact]
    public void IgnoresLegacyTempDirectoryConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:TempDirectory"] = Path.Combine(Path.GetTempPath(), "ignored-" + Guid.NewGuid().ToString("N"))
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value;

        Assert.Equal(Path.Combine(Path.GetTempPath(), "flexyparse"), options.ResolveTempDirectory());
    }

    [Fact]
    public async Task WorkspacesAreIsolatedAndDisposalOnlyDeletesOwnFiles()
    {
        var root = new FlexyParseOptions().ResolveTempDirectory();
        await using var first = JobWorkspace.Create(root, "first.pdf");
        await using var second = JobWorkspace.Create(root, "second.pdf");
        await File.WriteAllTextAsync(first.SourcePath, "first");
        await File.WriteAllTextAsync(second.SourcePath, "second");

        Assert.Equal(root, Path.GetDirectoryName(first.Root));
        Assert.NotEqual(first.Root, second.Root);

        await first.DisposeAsync();

        Assert.False(Directory.Exists(first.Root));
        Assert.Equal("second", await File.ReadAllTextAsync(second.SourcePath));

        await second.DisposeAsync();

        Assert.False(Directory.Exists(second.Root));
        Assert.True(Directory.Exists(root));
    }
}
