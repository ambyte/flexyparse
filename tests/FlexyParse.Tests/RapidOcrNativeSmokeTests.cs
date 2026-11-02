using System.Text.Json;
using FlexyParse.DependencyInjection;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.RapidOcr;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit.Abstractions;

namespace FlexyParse.Tests;

public sealed class RapidOcrNativeSmokeTests(ITestOutputHelper output)
{
    [RapidOcrDownloadNativeFact]
    public async Task AutomaticEslavDownloadThenOfflineCacheRecognizesRussian()
    {
        var options = new FlexyParseOptions();
        options.Recognition.Provider = RecognitionProvider.RapidOcr;
        options.Recognition.Language = "en";
        var local = new RapidOcrOptions { ModelsDirectory = Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_RAPIDOCR_DOWNLOAD_CACHE")! };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(new ConfigurationBuilder().Build());
        services.AddRapidOcrRecognition(new ConfigurationBuilder().Build());
        services.Configure<RapidOcrOptions>(configured => configured.ModelsDirectory = local.ModelsDirectory);
        services.Configure<FlexyParseOptions>(configured => configured.Recognition = options.Recognition);
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<RapidOcrModelStore>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var paths = await store.EnsureAvailableAsync("eslav", cancellation.Token);
        Assert.NotNull(paths);
        output.WriteLine("Verified preset cache: " + local.ModelsDirectory);
        using var offline = RapidOcrModelStoreTests.OfflineStore(options, local);
        Assert.Equal(paths, await offline.EnsureAvailableAsync("eslav", cancellation.Token));
        Assert.Empty(local.DetectionModelPath);

        await using var workspace = JobWorkspace.Create(Path.GetTempPath(), "automatic-eslav.pdf");
        workspace.OcrLanguage = "eslav";
        using (var typeface = SKTypeface.FromFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")))
        using (var bitmap = new SKBitmap(1400, 220))
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(typeface, 64))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText("\u041f\u0420\u0418\u0412\u0415\u0422 \u0420\u041e\u0421\u0421\u0418\u042f 12345", 50, 130, SKTextAlign.Left, font, paint);
            using var stream = File.Create(Path.Combine(workspace.ImagesDirectory, "russian.png"));
            Assert.True(bitmap.Encode(stream, SKEncodedImageFormat.Png, 100));
        }

        await using var recognizer = new RapidOcrImageRecognizer(Microsoft.Extensions.Options.Options.Create(options.Recognition),
            Microsoft.Extensions.Options.Options.Create(local),
            new RapidOcrSessionFactory(), NullLogger<RapidOcrImageRecognizer>.Instance, offline);
        await recognizer.RecognizeAsync(workspace, new DocumentIndex
        {
            SourceFileName = workspace.SourceFileName,
            Metadata = new DocumentMetadata { PageCount = 1 },
            Images = [new ImageManifestEntry { Hash = "russian", RelativePath = "images/russian.png", MediaType = "image/png" }]
        }, cancellation.Token);
        Assert.False(File.Exists(workspace.RecognitionErrorPath("russian")));
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("russian"), cancellation.Token));
        var text = string.Join("\n", result.RootElement.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
        output.WriteLine("Offline preset recognizer OCR: " + text);
        AssertRussianText(text);
    }

    [RapidOcrEslavNativeFact]
    public async Task ExplicitEslavModelsRecognizeRussianThroughFactoryAndRecognizer()
    {
        var models = Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_RAPIDOCR_ESLAV_MODELS")!;
        var options = new FlexyParseOptions();
        options.Recognition.Provider = RecognitionProvider.RapidOcr;
        // Only eslav is provisioned: a Latin default proves the per-job Russian override is used.
        options.Recognition.Language = "en";
        var local = new RapidOcrOptions();
        local.DetectionModelPath = Path.Combine(models, "ch_PP-OCRv5_mobile_det.onnx");
        local.ClassificationModelPath = Path.Combine(models, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        local.Profiles["eslav"] = new()
        {
            RecognitionModelPath = Path.Combine(models, "eslav_PP-OCRv5_rec_mobile.onnx"),
            DictionaryPath = Path.Combine(models, "ppocrv5_eslav_dict.txt")
        };
        foreach (var path in new[] { local.DetectionModelPath, local.ClassificationModelPath,
                     local.Profiles["eslav"].RecognitionModelPath, local.Profiles["eslav"].DictionaryPath })
        {
            Assert.True(File.Exists(path), $"Explicitly provisioned native smoke asset is missing: {path}");
        }

        await using var workspace = JobWorkspace.Create(Path.GetTempPath(), "russian-native-smoke.pdf");
        workspace.OcrLanguage = "eslav";
        var imagePath = Path.Combine(workspace.ImagesDirectory, "russian.png");
        // Load the actual Windows font file, not a platform-dependent fallback that may lack Cyrillic.
        var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        Assert.True(File.Exists(fontPath), $"Windows Arial with Cyrillic coverage is required: {fontPath}");
        using (var typeface = SKTypeface.FromFile(fontPath))
        using (var bitmap = new SKBitmap(1400, 220))
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(typeface, 64))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            Assert.NotNull(typeface);
            canvas.Clear(SKColors.White);
            canvas.DrawText("ПРИВЕТ РОССИЯ 12345", 50, 130, SKTextAlign.Left, font, paint);
            using var stream = File.Create(imagePath);
            Assert.True(bitmap.Encode(stream, SKEncodedImageFormat.Png, 100));
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var factory = new RapidOcrSessionFactory();
        using (var session = factory.Create(local, local.Profiles["eslav"]))
        {
            var text = await session.RecognizeAsync(imagePath, cancellation.Token);
            output.WriteLine("Factory OCR: " + text);
            AssertRussianText(text);
        }

        using var store = RapidOcrModelStoreTests.OfflineStore(options, local);
        await using var recognizer = new RapidOcrImageRecognizer(
            Microsoft.Extensions.Options.Options.Create(options.Recognition), Microsoft.Extensions.Options.Options.Create(local),
            factory, NullLogger<RapidOcrImageRecognizer>.Instance, store);
        var index = new DocumentIndex
        {
            SourceFileName = workspace.SourceFileName,
            Metadata = new DocumentMetadata { PageCount = 1 },
            Images = [new ImageManifestEntry
            {
                Hash = "russian", RelativePath = "images/russian.png", MediaType = "image/png",
                Width = 1400, Height = 220
            }]
        };
        await recognizer.RecognizeAsync(workspace, index, cancellation.Token);
        Assert.False(File.Exists(workspace.RecognitionErrorPath("russian")), "Real recognizer produced an OCR error artifact.");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.RecognitionPath("russian"), cancellation.Token));
        var paragraphs = result.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(paragraphs);
        Assert.All(paragraphs, paragraph => Assert.Equal("paragraph", paragraph.GetProperty("type").GetString()));
        var recognized = string.Join("\n", paragraphs.Select(paragraph => paragraph.GetProperty("text").GetString()));
        output.WriteLine("Recognizer OCR: " + recognized);
        AssertRussianText(recognized);
        Assert.False(File.Exists(workspace.RecognitionPath("russian") + ".tmp"));
    }

    private static void AssertRussianText(string text)
    {
        Assert.Contains("ПРИВЕТ", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("РОССИЯ", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12345", text);
    }

    [RapidOcrNativeFact]
    public async Task ExplicitLatinModelsRunWithResolvedOnnxAndSkiaNativeAssets()
    {
        var models = Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_RAPIDOCR_MODELS")!;
        var options = new RapidOcrOptions
        {
            DetectionModelPath = Path.Combine(models, "ch_PP-OCRv5_mobile_det.onnx"),
            ClassificationModelPath = Path.Combine(models, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx")
        };
        var profile = new RapidOcrModelProfile
        {
            RecognitionModelPath = Path.Combine(models, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
            DictionaryPath = Path.Combine(models, "ppocrv5_latin_dict.txt")
        };
        using var session = new RapidOcrSessionFactory().Create(options, profile);
        var imagePath = Path.Combine(Path.GetTempPath(), "flexyparse-native-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var bitmap = new SKBitmap(1100, 200))
            using (var canvas = new SKCanvas(bitmap))
            using (var font = new SKFont(SKTypeface.Default, 48))
            using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
            {
                canvas.Clear(SKColors.White);
                canvas.DrawText("HELLO FLEXYPARSE 12345", 40, 110, SKTextAlign.Left, font, paint);
                using var stream = File.Create(imagePath);
                bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var text = await session.RecognizeAsync(imagePath, cancellation.Token);
            Assert.Contains("HELLO", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("12345", text);

            // Reuse the real session, but reject dimensions before bitmap allocation/inference.
            options.MaxImageDimension = 32;
            await Assert.ThrowsAsync<InvalidDataException>(() => session.RecognizeAsync(imagePath, cancellation.Token));
        }
        finally
        {
            File.Delete(imagePath);
        }
    }
}

public sealed class RapidOcrNativeFactAttribute : FactAttribute
{
    public RapidOcrNativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_RAPIDOCR_MODELS")))
        {
            Skip = "Set FLEXYPARSE_TEST_RAPIDOCR_MODELS to an explicitly provisioned RapidOcrNet PP-OCRv5 Latin models directory.";
        }
    }
}

public sealed class RapidOcrDownloadNativeFactAttribute : FactAttribute
{
    public RapidOcrDownloadNativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_RAPIDOCR_DOWNLOAD_CACHE")))
        {
            Skip = "Set FLEXYPARSE_TEST_RAPIDOCR_DOWNLOAD_CACHE to an external cache directory to allow real pinned downloads.";
        }
        else if (!OperatingSystem.IsWindows())
        {
            Skip = "The synthetic Russian native smoke requires Windows Arial.";
        }
    }
}

public sealed class RapidOcrEslavNativeFactAttribute : FactAttribute
{
    public RapidOcrEslavNativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_RAPIDOCR_ESLAV_MODELS")))
        {
            Skip = "Set FLEXYPARSE_TEST_RAPIDOCR_ESLAV_MODELS to an explicitly provisioned PP-OCRv5 eslav models directory.";
        }
        else if (!OperatingSystem.IsWindows())
        {
            Skip = "The synthetic Russian native smoke requires Windows Arial with Cyrillic coverage.";
        }
    }
}
