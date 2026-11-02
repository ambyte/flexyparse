namespace FlexyParse.Options;

public sealed class ImageLayoutOptions
{
    public bool Enabled { get; set; } = true;
    public string ModelPath { get; set; } = "models/yolov10m-doclaynet.onnx";
    public string ModelDownloadUrl { get; set; } = "https://huggingface.co/Oblix/yolov10m-doclaynet_ONNX_document-layout-analysis/resolve/ded498025f9b377ebb079bb6984c46967ada1505/onnx/model.onnx";
    public string ModelSha256 { get; set; } = "6c5d0f1e1a1b9059bc8351217dce35dd7b0cda0df2f7ec6f7a6b120eb4b3ca96";
    public double Confidence { get; set; } = 0.35;
    public double IoU { get; set; } = 0.5;
    public string ImageResize { get; set; } = "Proportional";
    public int MaxRegions { get; set; } = 64;
    public int MaxFigurePasses { get; set; } = 4;
    public int MaxPixels { get; set; } = 32_000_000;
    public int PaddingPixels { get; set; } = 4;
    public double MinContentCoverage { get; set; } = 0.98;
    public int MaxOverlapPixels { get; set; } = 4;
    public double MaxOverlapRatio { get; set; } = 0.05;
    public bool ResolveNestedRegions { get; set; } = true;
    public bool WhitespaceFallback { get; set; } = true;

    internal bool IsValid() => !Enabled
        || (!string.IsNullOrWhiteSpace(ModelPath)
            && Uri.TryCreate(ModelDownloadUrl, UriKind.Absolute, out var downloadUri)
            && downloadUri.Scheme == Uri.UriSchemeHttps && downloadUri.UserInfo.Length == 0
            && ModelSha256.Length == 64 && ModelSha256.All(Uri.IsHexDigit)
            && double.IsFinite(Confidence) && Confidence is > 0 and <= 1
            && double.IsFinite(IoU) && IoU is > 0 and <= 1
            && ImageResize is "Proportional" or "Stretched"
            && MaxRegions is >= 1 and <= 256
            && MaxFigurePasses is >= 0 and <= 16
            && MaxPixels is >= 1 and <= 64_000_000
            && PaddingPixels is >= 0 and <= 32
            && MaxOverlapPixels is >= 0 and <= 32
            && double.IsFinite(MaxOverlapRatio) && MaxOverlapRatio is >= 0 and <= 0.2
            && double.IsFinite(MinContentCoverage) && MinContentCoverage is > 0 and <= 1);
}
