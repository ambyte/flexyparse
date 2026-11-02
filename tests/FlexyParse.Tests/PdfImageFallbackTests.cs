using System.Globalization;
using System.Text;
using System.Text.Json;
using FlexyParse.Extraction;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace FlexyParse.Tests;

public sealed class PdfImageFallbackTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public async Task ExtractsJpeg2000ThroughRegionRendering(int rotation, bool cropped)
    {
        var options = CreateOptions();
        await using var workspace = JobWorkspace.Create(options.Value.ResolveTempDirectory(), "jpx.pdf");
        var pdf = BuildJpeg2000Pdf(rotation, cropped);
        await File.WriteAllBytesAsync(workspace.SourcePath, pdf);
        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        var source = Assert.Single(page.GetImages());
        Assert.False(source.TryGetPng(out _));

        var images = new Dictionary<string, ImageManifestEntry>();
        var logger = new RecordingLogger<PdfImageExtractor>();
        var extractor = new PdfImageExtractor(logger, new PdfImageFallbackRenderer(options));
        var piece = Assert.Single(extractor.Extract(page, workspace, images, 1_000_000));
        Assert.Null(piece.Note);
        var entry = Assert.Single(images.Values);
        Assert.Equal("image/png", entry.MediaType);
        Assert.Equal([1], entry.PageNumbers);
        Assert.Equal(40, entry.Width);
        Assert.Equal(40, entry.Height);
        Assert.Equal(40, piece.PixelWidth);
        Assert.Equal(40, piece.PixelHeight);
        Assert.Contains(logger.Entries, e => e.Message.Contains("PDFtoImage fallback", StringComparison.Ordinal));
        using var bitmap = SKBitmap.Decode(Path.Combine(workspace.Root, entry.RelativePath));
        Assert.NotNull(bitmap);
        // The output must contain the image, not a blank or incorrectly positioned page region.
        foreach (var (x, y) in new[] { (5, 5), (20, 20), (35, 35) })
        {
            var pixel = bitmap.GetPixel(x, y);
            Assert.True(pixel.Red > 240 && pixel.Green < 15 && pixel.Blue < 15,
                $"Unexpected pixel {pixel} at ({x}, {y}) for rotation {rotation}, cropped={cropped}.");
        }
    }

    [Fact]
    public async Task DoesNotRenderSupportedImagesAndDeduplicatesThem()
    {
        var options = CreateOptions();
        await using var workspace = JobWorkspace.Create(options.Value.ResolveTempDirectory(), "supported.pdf");
        var builder = new PdfDocumentBuilder();
        var pageBuilder = builder.AddPage(300, 400);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAoAAAAKCAIAAAACUFjqAAAAE0lEQVR4nGP4z8CAB+GT+z9ypQG3ymOdJh6tQAAAAABJRU5ErkJggg==");
        pageBuilder.AddPng(png, new PdfRectangle(10, 10, 50, 50));
        pageBuilder.AddPng(png, new PdfRectangle(70, 10, 110, 50));
        using var document = PdfDocument.Open(builder.Build());
        var logger = new RecordingLogger<PdfImageExtractor>();
        var extractor = new PdfImageExtractor(logger, new PdfImageFallbackRenderer(options));
        var images = new Dictionary<string, ImageManifestEntry>();

        // The source file does not exist: normal extraction must not invoke PDFtoImage at all.
        var pieces = extractor.Extract(document.GetPage(1), workspace, images, 1_000_000);
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.Null(piece.Note));
        Assert.Equal(pieces[0].ContentHash, pieces[1].ContentHash);
        Assert.Single(images);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(200, 16_000_000, 8192, 111)]
    [InlineData(600, 1024, 8192, 32)]
    [InlineData(200, 16_000_000, 20, 20)]
    public async Task RendersAtConfiguredScaleWithinRasterLimits(int dpi, int maxPixels, int maxDimension, int expectedSize)
    {
        var options = CreateOptions();
        options.Value.ImageRenderFallback.Dpi = dpi;
        options.Value.ImageRenderFallback.MaxPixels = maxPixels;
        options.Value.ImageRenderFallback.MaxDimension = maxDimension;
        await using var workspace = JobWorkspace.Create(options.Value.ResolveTempDirectory(), "scaled.pdf");
        var pdf = BuildJpeg2000Pdf();
        await File.WriteAllBytesAsync(workspace.SourcePath, pdf);
        using var document = PdfDocument.Open(pdf);
        var extractor = new PdfImageExtractor(NullLogger<PdfImageExtractor>.Instance, new PdfImageFallbackRenderer(options));
        var images = new Dictionary<string, ImageManifestEntry>();
        Assert.Null(Assert.Single(extractor.Extract(document.GetPage(1), workspace, images, 1_000_000)).Note);
        var entry = Assert.Single(images.Values);
        using var bitmap = SKBitmap.Decode(Path.Combine(workspace.Root, entry.RelativePath));
        Assert.Equal(expectedSize, bitmap.Width);
        Assert.Equal(expectedSize, bitmap.Height);
        var pixel = bitmap.GetPixel(expectedSize / 2, expectedSize / 2);
        Assert.True(pixel.Red > 240 && pixel.Green < 15 && pixel.Blue < 15);
    }

    [Fact]
    public async Task RegistersFallbackImageWithoutDuplicatingPageText()
    {
        var options = CreateOptions();
        await using var workspace = JobWorkspace.Create(options.Value.ResolveTempDirectory(), "jpx.pdf");
        await File.WriteAllBytesAsync(workspace.SourcePath, BuildJpeg2000Pdf());
        var extractor = new DocumentExtractor(options,
            new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance),
            new PdfImageExtractor(NullLogger<PdfImageExtractor>.Instance, new PdfImageFallbackRenderer(options)),
            new TabulaTableExtractor(NullLogger<TabulaTableExtractor>.Instance),
            NullLogger<DocumentExtractor>.Instance);

        var index = await extractor.ExtractAsync(workspace, CancellationToken.None);
        var entry = Assert.Single(index.Images);
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(workspace.PagePath(1)));
        var page = JsonSerializer.Deserialize<StoredPage>(stored, JsonDefaults.Options)!;
        var image = Assert.Single(page.Blocks.OfType<ImageBlock>());
        Assert.Equal(entry.Hash, image.ContentHash);
        Assert.DoesNotContain(page.Blocks, b => b is ErrorBlock);
        var pageJson = await File.ReadAllTextAsync(workspace.PagePath(1));
        Assert.Equal(1, pageJson.Split("Outside image", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task KeepsWarningWhenFallbackIsDisabled()
    {
        var options = CreateOptions();
        options.Value.ImageRenderFallback.Enabled = false;
        await using var workspace = JobWorkspace.Create(options.Value.ResolveTempDirectory(), "jpx.pdf");
        using var document = PdfDocument.Open(BuildJpeg2000Pdf());
        var images = new Dictionary<string, ImageManifestEntry>();
        var extractor = new PdfImageExtractor(NullLogger<PdfImageExtractor>.Instance, new PdfImageFallbackRenderer(options));

        var piece = Assert.Single(extractor.Extract(document.GetPage(1), workspace, images, 1_000_000));
        Assert.Equal("The image format is not supported for recognition.", piece.Note);
        Assert.Empty(images);
    }

    [Fact]
    public async Task KeepsWarningAndLogsWhenRenderingFails()
    {
        var options = CreateOptions();
        await using var workspace = JobWorkspace.Create(options.Value.ResolveTempDirectory(), "broken.pdf");
        await File.WriteAllTextAsync(workspace.SourcePath, "not a PDF");
        using var document = PdfDocument.Open(BuildJpeg2000Pdf());
        var logger = new RecordingLogger<PdfImageExtractor>();
        var extractor = new PdfImageExtractor(logger, new PdfImageFallbackRenderer(options));
        var images = new Dictionary<string, ImageManifestEntry>();

        var piece = Assert.Single(extractor.Extract(document.GetPage(1), workspace, images, 1_000_000));
        Assert.NotNull(piece.Note);
        Assert.Empty(images);
        Assert.Contains(logger.Entries, e => e.Message.Contains("PDF render fallback failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppliesExistingEncodedImageSizeLimit()
    {
        var options = CreateOptions();
        await using var workspace = JobWorkspace.Create(options.Value.ResolveTempDirectory(), "jpx.pdf");
        var pdf = BuildJpeg2000Pdf();
        await File.WriteAllBytesAsync(workspace.SourcePath, pdf);
        using var document = PdfDocument.Open(pdf);
        var extractor = new PdfImageExtractor(NullLogger<PdfImageExtractor>.Instance, new PdfImageFallbackRenderer(options));
        var images = new Dictionary<string, ImageManifestEntry>();
        var piece = Assert.Single(extractor.Extract(document.GetPage(1), workspace, images, 1));
        Assert.Equal("The image is larger than the recognition limit and was not sent to the model.", piece.Note);
        Assert.Empty(images);
    }

    [Fact]
    public void LimitsRasterDimensionsBeforeRendering()
    {
        var options = CreateOptions();
        options.Value.ImageRenderFallback.MaxPixels = 1000;
        options.Value.ImageRenderFallback.MaxDimension = 100;
        var renderer = new PdfImageFallbackRenderer(options);
        foreach (var box in new[] { new Box(0, 0, 10000, 10000), new Box(0, 0, 10000, .001) })
        {
            var (_, width, height) = renderer.GetRenderGeometry(10000, 10000, box);
            Assert.InRange(width, 1, 100);
            Assert.InRange(height, 1, 100);
            Assert.True((long)width * height <= 1000);
        }
    }

    [Fact]
    public void ClipsBoundsAndConvertsToTopLeftCoordinates()
    {
        var renderer = new PdfImageFallbackRenderer(CreateOptions());
        var (bounds, width, height) = renderer.GetRenderGeometry(200, 300, new Box(-10, 100, 40, 400));
        Assert.Equal(0, bounds.X);
        Assert.Equal(0, bounds.Y);
        Assert.Equal(40, bounds.Width);
        Assert.Equal(200, bounds.Height);
        Assert.Equal(40, width);
        Assert.Equal(200, height);
        Assert.Throws<InvalidOperationException>(() => renderer.GetRenderGeometry(200, 300, new Box(300, 0, 400, 10)));
        Assert.Throws<InvalidOperationException>(() => renderer.GetRenderGeometry(200, 300, new Box(double.NaN, 0, 40, 10)));
    }

    [Fact]
    public void RejectsInvalidSettingsAndHonorsCancellation()
    {
        var options = CreateOptions();
        options.Value.ImageRenderFallback.MaxPixels = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfImageFallbackRenderer(options));
        var renderer = new PdfImageFallbackRenderer(CreateOptions());
        Assert.Throws<OperationCanceledException>(() => renderer.Render("missing.pdf", 1, 200, 300,
            new Box(0, 0, 40, 40), new CancellationToken(true)));
    }

    private static IOptions<FlexyParseOptions> CreateOptions() => Microsoft.Extensions.Options.Options.Create(new FlexyParseOptions
    {
        ImageRenderFallback = new ImageRenderFallbackOptions { Dpi = 72 }
    });

    private static byte[] BuildJpeg2000Pdf(int rotation = 0, bool cropped = false)
    {
        // Self-contained 10x10 red JPEG 2000 fixture, generated with Pillow/OpenJPEG.
        var jpx = Convert.FromBase64String("AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAACgAAAAoAAwcHAAAAAAAPY29scgEAAAAAABAAAACoanAyY/9P/1EALwAAAAAACgAAAAoAAAAAAAAAAAAAAAoAAAAKAAAAAAAAAAAAAwcBAQcBAQcBAf9SAAwAAAABAAMEBAAB/1wADUBASEhQSEhQSEhQ/2QAJQABQ3JlYXRlZCBieSBPcGVuSlBFRyB2ZXJzaW9uIDIuNS40/5AACgAAAAAAJwAB/5PPtAwByr/fgBAIkN+AEAiQgICAgICAgICA/9k=");
        var content = Encoding.ASCII.GetBytes("q 40 0 0 40 70 80 cm /Im1 Do Q\nBT /F1 12 Tf 130 200 Td (Outside image) Tj ET\n");
        var crop = cropped ? "/CropBox [20 30 220 330]" : "";
        var objects = new[]
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] {crop} /Rotate {rotation} /Resources << /XObject << /Im1 5 0 R >> /Font << /F1 6 0 R >> >> /Contents 4 0 R >>"),
            StreamObject("", content),
            StreamObject("/Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode", jpx),
            Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
        };
        using var pdf = new MemoryStream();
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Position);
            Write($"{i + 1} 0 obj\n");
            pdf.Write(objects[i]);
            Write("\nendobj\n");
        }

        var xref = pdf.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }

        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static byte[] StreamObject(string dictionary, byte[] payload)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes($"<< {dictionary} /Length {payload.Length} >>\nstream\n"));
        stream.Write(payload);
        stream.Write(Encoding.ASCII.GetBytes("\nendstream"));
        return stream.ToArray();
    }
}
