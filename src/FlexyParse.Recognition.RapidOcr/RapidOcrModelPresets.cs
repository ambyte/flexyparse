namespace FlexyParse.RapidOcr;

// ONNX hashes: RapidAI/RapidOCR v3.10.0 python/rapidocr/default_models.yaml.
// Dictionary hashes computed from the matching official v3.10.0 paddle assets (raw bytes).
internal sealed record RapidOcrModelAsset(string FileName, string Url, string Sha256, long MaxBytes);

internal sealed record RapidOcrModelPreset(RapidOcrModelAsset Detection, RapidOcrModelAsset Classification,
    RapidOcrModelAsset Recognition, RapidOcrModelAsset Dictionary);

internal static class RapidOcrModelPresets
{
    private const string Root = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/";
    private static readonly RapidOcrModelAsset Detection = Onnx("det", "ch_PP-OCRv5_det_mobile",
        "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae");
    private static readonly RapidOcrModelAsset Classification = Onnx("cls", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile",
        "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7");

    internal static RapidOcrModelPreset Get(string profile) => profile switch
    {
        "arabic" => Create("arabic", "c1192e632d0baa9146ae5b756a0e635e3dc63c1733737ebfd1629e87144e9295",
            "7f92f7dbb9b75a4787a83bfb4f6d14a8ab515525130c9d40a9036f61cf6999e9"),
        "ch" => Create("ch", "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5",
            "d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b"),
        "ch_server" => Create("ch", "e09385400eaaaef34ceff54aeb7c4f0f1fe014c27fa8b9905d4709b65746562a",
            "d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b", "server"),
        "devanagari" => Create("devanagari", "d6f0a906580e3fa6b324a318718f1f31f268b6ea8ef985f91c2012a37f52c91e",
            "09c7440bfc5477e5c41052304b6b185aff8c4a5e8b2b4c23c1c706f6fe1ee9fc"),
        "el" => Create("el", "b4368bccd557123c702b7549fee6cd1e94b581337d1c9b65310f109131542b7f",
            "31defc62c0c3ad3674a82da6192226a2ba98ef4ff014a7045cb88d59f9c3de31"),
        "en" => Create("en", "c3461add59bb4323ecba96a492ab75e06dda42467c9e3d0c18db5d1d21924be8",
            "e025a66d31f327ba0c232e03f407ae8d105e1e709e7ccb3f408aa778c24e70d6"),
        "eslav" => Create("eslav", "08705d6721849b1347d26187f15a5e362c431963a2a62bfff4feac578c489aab",
            "3e95f1581557162870cacdba5af91a4c6be2890710d395b0c3c7578e7ee5e6eb"),
        "korean" => Create("korean", "cd6e2ea50f6943ca7271eb8c56a877a5a90720b7047fe9c41a2e541a25773c9b",
            "a88071c68c01707489baa79ebe0405b7beb5cca229f4fc94cc3ef992328802d7"),
        "latin" => Create("latin", "b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a",
            "3c0a8a79b612653c25f765271714f71281e4e955962c153e272b7b8c1d2b13ff"),
        "cyrillic" => Create("cyrillic", "90f761b4bfcce0c8c561c0cb5c887b0971d3ec01c32164bdf7374a35b0982711",
            "db40aa52ceb112055be80c694afdf655d5d2c4f7873704524cc16a447ca913ba"),
        "ta" => Create("ta", "a42448808b7dea87597336f12438935f40353f1949e8360acd9e06b4da21bfe1",
            "85b541352ae18dc6ba6d47152d8bf8adff6b0266e605d2eef2990c1bf466117b"),
        "te" => Create("te", "a3690451b50028a09a3316a1274f7c05728151ea3f8fd392696397a7fefcbd92",
            "42f83f5d3fdb50778e4fa5b66c58d99a59ab7792151c5e74f34b8ffd7b61c9d6"),
        "th" => Create("th", "de541dd83161c241ff426f7ecfd602a0ba77d686cf3ab9a6c255ea82fd08006e",
            "57f5406f94bb6688fb7077f7be65f08bbd71cecf48c01ea26c522cb5c4836b7a"),
        _ => throw new ArgumentException("Unknown RapidOCR preset.", nameof(profile))
    };

    private static RapidOcrModelPreset Create(string profile, string modelHash, string dictionaryHash, string variant = "mobile")
    {
        var name = profile + "_PP-OCRv5_rec_" + variant;
        var dictionary = profile == "ch" ? "ppocrv5_dict.txt" : "ppocrv5_" + profile + "_dict.txt";
        return new(Detection, Classification, Onnx("rec", name, modelHash),
            new(dictionary, Root + "paddle/PP-OCRv5/rec/" + name + "/" + dictionary, dictionaryHash, 1024 * 1024));
    }

    private static RapidOcrModelAsset Onnx(string task, string name, string hash) =>
        new(name + ".onnx", Root + "onnx/PP-OCRv5/" + task + "/" + name + ".onnx", hash, 128L * 1024 * 1024);
}
