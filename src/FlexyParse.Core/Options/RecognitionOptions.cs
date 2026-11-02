using Microsoft.Extensions.Options;

namespace FlexyParse.Options;

public enum RecognitionProvider
{
    OpenAi,
    RapidOcr,
    NemotronCPU,
    NemotronGPU
}

public sealed class RecognitionOptions
{
    public RecognitionProvider Provider { get; set; } = RecognitionProvider.OpenAi;

    public string Language { get; set; } = "eslav";
}

internal sealed class RecognitionOptionsValidator : IValidateOptions<FlexyParseOptions>
{
    public ValidateOptionsResult Validate(string? name, FlexyParseOptions options) =>
        Enum.IsDefined(options.Recognition.Provider)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Recognition:Provider must be OpenAi, RapidOcr, NemotronCPU or NemotronGPU.");
}
