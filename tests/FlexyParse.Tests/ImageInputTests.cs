using System.Text;
using System.Text.Json;
using FlexyParse.DependencyInjection;
using FlexyParse.Extraction;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using FlexyParse.Recognition;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace FlexyParse.Tests;

public sealed class ImageInputTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "scan.PNG", OutputFormat.Json, true)]
    [InlineData(SKEncodedImageFormat.Jpeg, "scan.jpeg", OutputFormat.Markdown, true)]
    [InlineData(SKEncodedImageFormat.Webp, "scan.webp", OutputFormat.Html, true)]
    [InlineData(SKEncodedImageFormat.Png, "scan.bin", OutputFormat.Json, false)]
    public async Task ImagesBypassGotenbergAndUseExistingRecognition(
        SKEncodedImageFormat format, string fileName, OutputFormat outputFormat, bool gotenbergEnabled)
    {
        var bytes = Encode(format);
        var recognizer = new RecordingRecognizer();
        using var provider = CreateProvider(recognizer, gotenbergEnabled);
        await using var source = new MemoryStream(bytes);
        await using var destination = new MemoryStream();
        await provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
        {
            Content = source, FileName = fileName, Format = outputFormat, OcrLanguage = "latin"
        }, destination, CancellationToken.None);

        Assert.Equal(1, recognizer.Calls);
        Assert.Equal("latin", recognizer.Language);
        Assert.Equal(bytes, recognizer.Payload);
        Assert.Contains("Recognized image", Encoding.UTF8.GetString(destination.ToArray()), StringComparison.Ordinal);
        Assert.False(Directory.Exists(recognizer.Root));
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task RecognitionCanBeDisabledWithoutCallingGotenberg()
    {
        var recognizer = new RecordingRecognizer();
        using var provider = CreateProvider(recognizer, gotenbergEnabled: true);
        await using var source = new MemoryStream(Encode(SKEncodedImageFormat.Png));
        await using var destination = new MemoryStream();
        await provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
        {
            Content = source, FileName = "scan.png", Format = OutputFormat.Json, RecognizeImages = false
        }, destination, CancellationToken.None);
        Assert.Equal(0, recognizer.Calls);
        using var result = JsonDocument.Parse(destination.ToArray());
        Assert.Single(result.RootElement.GetProperty("pages").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiskImagesHonorMoveSource(bool move)
    {
        var root = Path.Combine(Path.GetTempPath(), "flexyparse-image-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "scan.png");
            await File.WriteAllBytesAsync(path, Encode(SKEncodedImageFormat.Png));
            var recognizer = new RecordingRecognizer();
            using var provider = CreateProvider(recognizer, false);
            await using var destination = new MemoryStream();
            await provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
            {
                SourcePath = path, MoveSource = move, FileName = "scan.png", Format = OutputFormat.Json
            }, destination, CancellationToken.None);
            Assert.Equal(!move, File.Exists(path));
            Assert.Equal(1, recognizer.Calls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TransparentImagesAreFlattenedOnWhiteWithoutResizing()
    {
        using var bitmap = new SKBitmap(12, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(2, 3, SKColors.Black);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await using var workspace = await CreateWorkspaceAsync(encoded.ToArray(), "scan.png");
        var index = await Extractor().TryExtractAsync(workspace, CancellationToken.None);
        var image = Assert.Single(index!.Images);
        using var normalized = SKBitmap.Decode(Path.Combine(workspace.Root, image.RelativePath));
        Assert.Equal(12, normalized.Width);
        Assert.Equal(8, normalized.Height);
        Assert.Equal(SKColors.White, normalized.GetPixel(0, 0));
        Assert.Equal(SKColors.Black, normalized.GetPixel(2, 3));
        Assert.Equal("image/png", image.MediaType);
    }

    [Fact]
    public async Task BmpIsNormalizedToPng()
    {
        // A 2x1, 24-bit BMP containing a red pixel followed by a blue pixel.
        var bytes = new byte[62];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BitConverter.GetBytes(62).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(2).CopyTo(bytes, 18);
        BitConverter.GetBytes(1).CopyTo(bytes, 22);
        bytes[26] = 1; bytes[28] = 24;
        bytes[56] = 255; bytes[57] = 255;
        await using var workspace = await CreateWorkspaceAsync(bytes, "scan.bmp");
        var index = await Extractor().TryExtractAsync(workspace, CancellationToken.None);
        var image = Assert.Single(index!.Images);
        Assert.Equal("image/png", image.MediaType);
        using var normalized = SKBitmap.Decode(Path.Combine(workspace.Root, image.RelativePath));
        Assert.Equal(SKColors.Red, normalized.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, normalized.GetPixel(1, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task AppliesAllExifOrientations(int orientation)
    {
        using var original = new SKBitmap(24, 16, SKColorType.Bgra8888, SKAlphaType.Opaque);
        original.Erase(SKColors.Blue);
        using (var canvas = new SKCanvas(original))
        using (var paint = new SKPaint { Color = SKColors.Red })
        {
            canvas.DrawRect(0, 0, 12, 8, paint);
        }

        using var encoded = original.Encode(SKEncodedImageFormat.Jpeg, 100);
        var bytes = AddExifOrientation(encoded.ToArray(), orientation);
        await using var workspace = await CreateWorkspaceAsync(bytes, "photo.jpg");
        var index = await Extractor().TryExtractAsync(workspace, CancellationToken.None);
        var image = Assert.Single(index!.Images);
        using var normalized = SKBitmap.Decode(Path.Combine(workspace.Root, image.RelativePath));
        Assert.Equal(orientation >= 5 ? 16 : 24, normalized.Width);
        Assert.Equal(orientation >= 5 ? 24 : 16, normalized.Height);
        var redRight = orientation is 2 or 3 or 6 or 7;
        var redBottom = orientation is 3 or 4 or 7 or 8;
        var pixel = normalized.GetPixel(redRight ? normalized.Width - 3 : 2, redBottom ? normalized.Height - 3 : 2);
        Assert.True(pixel.Red > 200 && pixel.Blue < 50);
        Assert.Equal(orientation == 1 ? "image/jpeg" : "image/png", image.MediaType);
    }

    [Theory]
    [InlineData("broken.png")]
    [InlineData("broken.JPG")]
    [InlineData("unsupported.tiff")]
    public async Task InvalidImageDoesNotFallBackToGotenberg(string fileName)
    {
        using var provider = CreateProvider(new RecordingRecognizer(), true);
        await using var source = new MemoryStream("not an image"u8.ToArray());
        await using var destination = new MemoryStream();
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() =>
            provider.GetRequiredService<IDocumentConverter>().ConvertAsync(new ConversionInput
            {
                Content = source, FileName = fileName, Format = OutputFormat.Json
            }, destination, CancellationToken.None));
        Assert.Equal(ConversionStatus.BadRequest, error.StatusCode);
    }

    [Theory]
    [InlineData(8, 40000000, 1000000)]
    [InlineData(16384, 32, 1000000)]
    [InlineData(16384, 40000000, 16)]
    public async Task EnforcesImageResourceLimits(int dimension, long pixels, int bytes)
    {
        await using var workspace = await CreateWorkspaceAsync(Encode(SKEncodedImageFormat.Png), "scan.png");
        var options = new FlexyParseOptions
        {
            ImageInput = new ImageInputOptions { MaxDimension = dimension, MaxPixels = pixels }
        };
        options.ModelApi.MaxImageBytes = bytes;
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() =>
            Extractor(options).TryExtractAsync(workspace, CancellationToken.None));
        Assert.Equal(ConversionStatus.PayloadTooLarge, error.StatusCode);
        Assert.Empty(Directory.GetFiles(workspace.ImagesDirectory));
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeDecoding()
    {
        await using var workspace = await CreateWorkspaceAsync(Encode(SKEncodedImageFormat.Png), "scan.png");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Extractor().TryExtractAsync(workspace, cancellation.Token));
        Assert.Empty(Directory.GetFiles(workspace.ImagesDirectory));
    }

    [Fact]
    public async Task MultiFrameGifIsRejectedInsteadOfDroppingFrames()
    {
        byte[] header = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 255, 255, 255];
        byte[] frame = [0x21, 0xf9, 4, 0, 1, 0, 0, 0, 0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 1, 0];
        await using var workspace = await CreateWorkspaceAsync([.. header, .. frame, .. frame, 0x3b], "animated.gif");
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() => Extractor().TryExtractAsync(workspace, CancellationToken.None));
        Assert.Equal(ConversionStatus.BadRequest, error.StatusCode);
        Assert.Contains("Multi-frame", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleFrameGifIsNormalizedToPng()
    {
        var bytes = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
        await using var workspace = await CreateWorkspaceAsync(bytes, "scan.gif");
        var index = await Extractor().TryExtractAsync(workspace, CancellationToken.None);
        Assert.Equal("image/png", Assert.Single(index!.Images).MediaType);
    }

    private static ImageDocumentExtractor Extractor(FlexyParseOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? new FlexyParseOptions()));

    private static byte[] Encode(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(24, 16, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.White);
        using var encoded = bitmap.Encode(format, 100);
        return encoded.ToArray();
    }

    private static byte[] AddExifOrientation(byte[] jpeg, int orientation)
    {
        byte[] exif = [0xff, 0xe1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 1, 3, 0, 1, 0, 0, 0,
            (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        return [.. jpeg[..2], .. exif, .. jpeg[2..]];
    }

    private static async Task<JobWorkspace> CreateWorkspaceAsync(byte[] bytes, string fileName)
    {
        var workspace = JobWorkspace.Create(Path.Combine(Path.GetTempPath(), "flexyparse-image-tests"), fileName);
        await using var source = new MemoryStream(bytes);
        await workspace.SaveSourceAsync(source, 1_000_000, CancellationToken.None);
        return workspace;
    }

    private static ServiceProvider CreateProvider(RecordingRecognizer recognizer, bool gotenbergEnabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:GotenbergUrl"] = gotenbergEnabled ? "http://gotenberg:3000" : "",
            ["FlexyParse:ModelApi:RequireApiKey"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(configuration);
        services.AddSingleton<IImageRecognizer>(recognizer);
        services.AddHttpClient(GotenbergPdfConverter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new RejectingHandler());
        return services.BuildServiceProvider();
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Images must not call Gotenberg.");
    }

    private sealed class RecordingRecognizer : IImageRecognizer
    {
        public int Calls { get; private set; }
        public string? Language { get; private set; }
        public string? Root { get; private set; }
        public byte[]? Payload { get; private set; }

        public async Task RecognizeAsync(JobWorkspace workspace, DocumentIndex index, CancellationToken cancellationToken)
        {
            Calls++;
            Root = workspace.Root;
            Language = workspace.OcrLanguage;
            var image = Assert.Single(index.Images);
            Assert.Equal([1], image.PageNumbers);
            Assert.Equal(1, index.Metadata.PageCount);
            Payload = await File.ReadAllBytesAsync(Path.Combine(workspace.Root, image.RelativePath), cancellationToken);
            await File.WriteAllTextAsync(workspace.RecognitionPath(image.Hash),
                """[{"type":"paragraph","text":"Recognized image"}]""", cancellationToken);
        }
    }
}
