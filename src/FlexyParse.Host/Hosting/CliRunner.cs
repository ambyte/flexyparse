using FlexyParse.Cli;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using Microsoft.Extensions.Options;

namespace FlexyParse.Hosting;

public sealed class CliRunner
{
    private readonly IDocumentConverter _converter;
    private readonly FlexyParseOptions _options;
    private readonly ILogger<CliRunner> _logger;

    public CliRunner(IDocumentConverter converter, IOptions<FlexyParseOptions> options, ILogger<CliRunner> logger)
    {
        _converter = converter;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!CliArguments.TryParse(args, out var command, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(CliArguments.Help);
            return 2;
        }

        if (command.Kind == CliCommandKind.Help)
        {
            Console.WriteLine(CliArguments.Help);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(command.OutputDirectory))
        {
            Directory.CreateDirectory(command.OutputDirectory);
        }

        var parallelism = Math.Min(_options.ResolveDocumentParallelism(), command.Files.Count);
        var failures = 0;
        await Parallel.ForEachAsync(
            command.Files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken
            },
            async (file, token) =>
            {
                if (!await ConvertOneAsync(file, command, token).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref failures);
                }
            }).ConfigureAwait(false);

        return failures == 0 ? 0 : 1;
    }

    private async Task<bool> ConvertOneAsync(string file, CliCommand command, CancellationToken cancellationToken)
    {
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"File not found: {file}");
            return false;
        }

        var outputPath = CliArguments.ResolveOutputPath(file, command);
        var stdout = outputPath == "-";
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!stdout && string.Equals(Path.GetFullPath(file), Path.GetFullPath(outputPath), pathComparison))
        {
            Console.Error.WriteLine($"{file}: The input and output paths are the same. Specify a different path via --output or --output-dir.");
            return false;
        }

        Stream destination;
        if (stdout)
        {
            destination = Console.OpenStandardOutput();
        }
        else
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        }

        try
        {
            await using var source = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            await _converter.ConvertAsync(
                new ConversionInput
                {
                    Content = source,
                    FileName = Path.GetFileName(file),
                    Format = command.Format,
                    RecognizeImages = command.RecognizeImages,
                    OcrLanguage = command.OcrLanguage
                },
                destination,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DocumentConversionException exception)
        {
            _logger.LogError(exception, "Conversion failed for {File}", file);
            Console.Error.WriteLine($"{file}: {exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Conversion failed for {File}", file);
            Console.Error.WriteLine($"{file}: Failed to parse the document.");
            return false;
        }
        finally
        {
            if (!stdout)
            {
                await destination.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
