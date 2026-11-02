using FlexyParse.Pipeline;
using FlexyParse.Recognition;

namespace FlexyParse.Cli;

public enum CliCommandKind
{
    Help,
    Convert
}

public sealed class CliCommand
{
    public required CliCommandKind Kind { get; init; }

    public IReadOnlyList<string> Files { get; init; } = [];

    public OutputFormat Format { get; init; } = OutputFormat.Json;

    public string? Output { get; init; }

    public string? OutputDirectory { get; init; }

    public bool? RecognizeImages { get; init; }

    public string? OcrLanguage { get; init; }
}

public static class CliArguments
{
    public const string Help =
        """
        FlexyParse accepts PDF, Markdown and raster images and returns JSON, Markdown or HTML.

        flexyparse
            Starts the HTTP service.

        flexyparse serve [--urls http://localhost:8080]
            Starts the HTTP service.

        flexyparse convert <file.pdf|file.md|image.png> [more files ...] [options]
            --format, -f          json, markdown or html. Default is json.
            --output, -o          Output file. «-» writes one file to stdout.
            --output-dir, -d      Directory for multiple files.
            --recognize-images    true or false. Default is taken from configuration.
            --ocr-language        RapidOCR model prefix (for example eslav, latin, ch_server).
                                  Only model prefixes are accepted; no language aliases or regional tags.
                                  Default: FLEXYPARSE_RAPIDOCR_LANGUAGE / Recognition:Language (eslav).

        Without --output and --output-dir, the result is written next to the source file.
        Multiple files are parsed in parallel, within MaxConcurrentDocuments.

        POST /v1/convert/file
            multipart/form-data: files, to_formats (md/json/html), do_ocr, ocr_language
            One file — a JSON response; several files — a ZIP archive
        POST /v1/convert/source
            JSON: http_sources (URL), file_sources (filename/base64_string), options
        GET /v1/capabilities
        GET /health

        Model API Key: FLEXYPARSE_MODEL_API_KEY, MODEL_API_KEY or FlexyParse__ModelApi__ApiKey.
        """ + "\n\nRapidOCR models and supported languages:\n" + OcrLanguageProfiles.ModelLanguagesDescription;

    public static bool TryParse(IReadOnlyList<string> args, out CliCommand command, out string? error)
    {
        command = new CliCommand { Kind = CliCommandKind.Help };
        error = null;
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            return true;
        }

        if (args[0] is not "convert")
        {
            error = $"Unknown command «{args[0]}».";
            return false;
        }

        var files = new List<string>();
        OutputFormat format = OutputFormat.Json;
        string? output = null;
        string? outputDirectory = null;
        bool? recognizeImages = null;
        string? ocrLanguage = null;

        for (var index = 1; index < args.Count; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "--format" or "-f":
                    if (!TryTake(args, ref index, out var formatValue) || !TryParseFormat(formatValue, out format))
                    {
                        error = "Specify the format: json, markdown or html.";
                        return false;
                    }

                    break;
                case "--output" or "-o":
                    if (!TryTake(args, ref index, out output))
                    {
                        error = "Specify the output file after --output.";
                        return false;
                    }

                    break;
                case "--output-dir" or "-d":
                    if (!TryTake(args, ref index, out outputDirectory))
                    {
                        error = "Specify the output directory after --output-dir.";
                        return false;
                    }

                    break;
                case "--ocr-language":
                    if (!TryTake(args, ref index, out var language))
                    {
                        error = "Specify a language after --ocr-language.";
                        return false;
                    }

                    try
                    {
                        ocrLanguage = OcrLanguageProfiles.Normalize(language);
                    }
                    catch (ArgumentException exception)
                    {
                        error = exception.Message;
                        return false;
                    }

                    break;
                case "--recognize-images":
                    if (!TryTake(args, ref index, out var recognizeValue) || !TryParseBool(recognizeValue, out var recognize))
                    {
                        error = "Specify true or false after --recognize-images.";
                        return false;
                    }

                    recognizeImages = recognize;
                    break;
                default:
                    if (token.StartsWith('-'))
                    {
                        error = $"Unknown parameter «{token}».";
                        return false;
                    }

                    files.Add(token);
                    break;
            }
        }

        if (files.Count == 0)
        {
            error = "Specify at least one PDF or Markdown file.";
            return false;
        }

        if (files.Count > 1 && output is not null)
        {
            error = "For multiple files, use --output-dir instead of --output.";
            return false;
        }

        if (output == "-" && files.Count > 1)
        {
            error = "In stdout you can output only one file.";
            return false;
        }

        command = new CliCommand
        {
            Kind = CliCommandKind.Convert,
            Files = files,
            Format = format,
            Output = output,
            OutputDirectory = outputDirectory,
            RecognizeImages = recognizeImages,
            OcrLanguage = ocrLanguage
        };
        return true;
    }

    public static bool TryParseFormat(string? value, out OutputFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "json":
                format = OutputFormat.Json;
                return true;
            case "markdown" or "md":
                format = OutputFormat.Markdown;
                return true;
            case "html":
                format = OutputFormat.Html;
                return true;
            default:
                format = OutputFormat.Json;
                return false;
        }
    }

    public static bool TryParseBool(string? value, out bool parsed)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes":
                parsed = true;
                return true;
            case "false" or "0" or "no":
                parsed = false;
                return true;
            default:
                parsed = false;
                return false;
        }
    }

    public static string Extension(OutputFormat format)
    {
        return format switch
        {
            OutputFormat.Markdown => ".md",
            OutputFormat.Html => ".html",
            _ => ".json"
        };
    }

    public static string ResolveOutputPath(string sourcePath, CliCommand command)
    {
        var extension = Extension(command.Format);
        if (command.Output == "-")
        {
            return "-";
        }

        if (!string.IsNullOrWhiteSpace(command.Output))
        {
            return command.Output;
        }

        var fileName = Path.GetFileNameWithoutExtension(sourcePath) + extension;
        if (!string.IsNullOrWhiteSpace(command.OutputDirectory))
        {
            return Path.Combine(command.OutputDirectory, fileName);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        return Path.Combine(directory ?? Directory.GetCurrentDirectory(), fileName);
    }

    private static bool TryTake(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count)
        {
            value = "";
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
