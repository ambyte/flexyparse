using FlexyParse.Options;
using FlexyParse.Recognition;
using Microsoft.Extensions.Options;

namespace FlexyParse.RapidOcr;

public sealed class RapidOcrOptions
{
    public const string SectionName = "FlexyParse:Recognition:RapidOcr";

    public string ModelsDirectory { get; set; } = "models/rapidocr/v3.10.0";
    public string DetectionModelPath { get; set; } = "";
    public string ClassificationModelPath { get; set; } = "";

    /// <summary>Global CPU inference limit across jobs. Each profile also serializes its session.</summary>
    public int MaxConcurrency { get; set; } = 1;
    public int InferenceThreads { get; set; } = 1;
    public int MaxImageDimension { get; set; } = 16384;
    public long MaxImagePixels { get; set; } = 40_000_000;
    /// <summary>Optional local overrides keyed by model prefix. Missing profiles use pinned presets.</summary>
    public Dictionary<string, RapidOcrModelProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns configured overrides or empty paths for a supported preset without modifying options.</summary>
    public RapidOcrModelProfile GetProfile(string profile)
    {
        var name = OcrLanguageProfiles.Normalize(profile);
        return Profiles.TryGetValue(name, out var configured) ? configured : new();
    }
}

public sealed class RapidOcrModelProfile
{
    public string RecognitionModelPath { get; set; } = "";
    public string DictionaryPath { get; set; } = "";
}

internal sealed class RapidOcrOptionsValidator(IOptions<RecognitionOptions> recognitionOptions) : IValidateOptions<RapidOcrOptions>
{
    public ValidateOptionsResult Validate(string? name, RapidOcrOptions options)
    {
        var recognition = recognitionOptions.Value;
        // OpenAI must not depend on local models or local language support.
        if (recognition.Provider != RecognitionProvider.RapidOcr)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        try
        {
            _ = OcrLanguageProfiles.Normalize(recognition.Language);
        }
        catch (ArgumentException exception)
        {
            errors.Add(exception.Message);
        }

        if (string.IsNullOrWhiteSpace(options.ModelsDirectory))
        {
            errors.Add("Recognition:RapidOcr:ModelsDirectory must not be empty.");
        }
        if (options.MaxConcurrency is < 1 or > 16 || options.InferenceThreads is < 1 or > 64
            || options.MaxImageDimension is < 32 or > 65536 || options.MaxImagePixels <= 0)
        {
            errors.Add("Recognition:RapidOcr requires MaxConcurrency 1..16, InferenceThreads 1..64, MaxImageDimension 32..65536 and positive MaxImagePixels.");
        }

        CheckFile(options.DetectionModelPath, "DetectionModelPath", errors);
        CheckFile(options.ClassificationModelPath, "ClassificationModelPath", errors);
        foreach (var (profileName, profile) in options.Profiles)
        {
            var setting = "Profiles:" + profileName;
            if (!OcrLanguageProfiles.ModelNames.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Recognition:RapidOcr:{setting} is an unknown model profile.");
                continue;
            }
            if (profile is null)
            {
                errors.Add($"Recognition:RapidOcr:{setting} must contain model profile settings.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(profile.RecognitionModelPath) != string.IsNullOrWhiteSpace(profile.DictionaryPath))
            {
                errors.Add($"Recognition:RapidOcr:{setting} custom recognition model and dictionary must be configured together.");
            }
            else
            {
                CheckFile(profile.RecognitionModelPath, setting + ":RecognitionModelPath", errors);
                CheckFile(profile.DictionaryPath, setting + ":DictionaryPath", errors);
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckFile(string path, string setting, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            errors.Add($"Recognition:RapidOcr:{setting} must reference an existing local file.");
        }
    }
}
