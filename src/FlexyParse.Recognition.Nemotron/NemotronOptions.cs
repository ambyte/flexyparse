namespace FlexyParse.Nemotron;

public enum NemotronExecutionMode
{
    CpuInt8,
    WebGpuFp16
}

/// <summary>Settings for the pinned XDcobra Nemotron-3.5-Parse ONNX CPU/GPU packs.</summary>
public sealed class NemotronOptions
{
    public const string SectionName = "FlexyParse:Recognition:Nemotron";

    public NemotronExecutionMode ExecutionMode { get; set; } = NemotronExecutionMode.CpuInt8;
    /// <summary>Zero-based index in the list of devices exposed by the WebGPU plugin.</summary>
    public int GpuDeviceId { get; set; }
    // Empty selects a mode-specific cache; an explicit path points to a complete pack.
    public string ModelsDirectory { get; set; } = "";
    internal string PackVariant => ExecutionMode == NemotronExecutionMode.WebGpuFp16 ? "fp16" : "int8";
    internal string ResolvedModelsDirectory => string.IsNullOrEmpty(ModelsDirectory)
        ? $"models/nemotron/275c486f-{PackVariant}" : ModelsDirectory;
    public bool DownloadModels { get; set; } = true;
    public int DownloadTimeoutSeconds { get; set; } = 1800;
    public int InferenceThreads { get; set; } = 4;
    public int RepetitionDetectionMinTokens { get; set; } = 512;
    public int RepetitionDetectionMinRepeats { get; set; } = 8;
    public int RepetitionDetectionMaxPeriod { get; set; } = 256;
    public float RepetitionPenalty { get; set; } = 1.1f;
    public bool RetryRepetitiveImages { get; set; } = true;
    public bool UpscaleRepetitiveImages { get; set; } = true;
    public int RepetitionRecoveryScale { get; set; } = 3;
    public int RepetitionRecoveryPaddingPixels { get; set; } = 12;
    public int RepetitionRecoveryMaxPixels { get; set; } = 1_000_000;
    public int RepetitionRecoveryMaxRegions { get; set; } = 8;
    public int RepetitionRecoveryTimeoutSeconds { get; set; } = 60;
    public int MaxImageDimension { get; set; } = 16384;
    public long MaxImagePixels { get; set; } = 40000000;
    public int MaxHeight { get; set; } = 2048;
    public int MaxWidth { get; set; } = 1664;

    internal bool IsValid() => ModelsDirectory is not null
        && (ModelsDirectory.Length == 0 || !string.IsNullOrWhiteSpace(ModelsDirectory))
        && Enum.IsDefined(ExecutionMode) && GpuDeviceId >= 0
        && DownloadTimeoutSeconds is >= 1 and <= 86400
        && InferenceThreads is >= 1 and <= 256
        && RepetitionDetectionMinTokens is >= 128 and <= 8192
        && RepetitionDetectionMinRepeats is >= 4 and <= 16
        && RepetitionDetectionMaxPeriod is >= 1 and <= 512
        && float.IsFinite(RepetitionPenalty) && RepetitionPenalty is >= 1 and <= 2
        && RepetitionRecoveryScale is >= 1 and <= 4
        && RepetitionRecoveryPaddingPixels is >= 0 and <= 32
        && RepetitionRecoveryMaxPixels is >= 4096 and <= 4_000_000
        && RepetitionRecoveryMaxRegions is >= 1 and <= 16
        && RepetitionRecoveryTimeoutSeconds is >= 1 and <= 600
        && MaxImageDimension is >= 64 and <= 65536
        && MaxImagePixels is >= 4096 and <= 100000000
        && MaxHeight is >= 16 and <= 2048 && MaxHeight % 16 == 0
        && MaxWidth is >= 64 and <= 1664 && MaxWidth % 64 == 0;
}
