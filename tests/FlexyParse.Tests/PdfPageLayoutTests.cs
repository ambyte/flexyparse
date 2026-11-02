using System.Text.Json;
using System.Text;
using FlexyParse.DependencyInjection;
using FlexyParse.Extraction;
using FlexyParse.ImageLayout;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Output;
using FlexyParse.Recognition;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FlexyParse.Tests;

public class PdfPageLayoutTests
{
    [Fact]
    public void MapsRenderCoordinatesAndClipsEdgesWithoutDoubleRotation()
    {
        var regions = PdfPageLayoutAnalyzer.MapRegions(
            [Region("Table", -2, 100, 400, 300)], 800, 1000, 400, 500, 0.25);
        Assert.Equal(new Box(0, 350, 200, 450), Assert.Single(regions).Box);
    }

    [Fact]
    public void StrongerTextLabelWinsOverCompetingTableLabel()
    {
        var regions = PdfPageLayoutAnalyzer.MapRegions(
            [Region("Table", 10, 10, 300, 200, 0.7), Region("Text", 10, 10, 300, 200, 0.9),
                Region("Table", 0, 0, 0, 100), Region("Table", 20, 20, 50, 50, double.NaN),
                Region("Table", -1000, -1000, 1000, 1000)],
            400, 500, 400, 500, 0.25);
        Assert.Equal("text", Assert.Single(regions).Kind);
    }

    [Fact]
    public void SingleTableDetectionIsUsefulWithoutImageSplittingOrCoverageChecks()
    {
        var regions = PdfPageLayoutAnalyzer.MapRegions([Region("Table", 40, 300, 240, 400)],
            400, 500, 400, 500, 0.25);
        Assert.Equal("table", Assert.Single(regions).Kind);
    }

    [Fact]
    public void AuthorsAndAlignedProseAreNotTablesWithoutVisualConfirmation()
    {
        using var document = PdfDocument.Open(BuildPdf(grid: false));
        var extractor = TableExtractor();
        Assert.Empty(extractor.Extract(document, 1));
        Assert.Empty(extractor.Extract(document, 1, []));

        var text = TextExtractor().Extract(document.GetPage(1));
        var combined = string.Join(" ", text.Select(piece => piece.Text));
        Assert.Contains("Alice", combined, StringComparison.Ordinal);
        Assert.Contains("Bob", combined, StringComparison.Ordinal);
        Assert.Contains("Conclusion", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void BorderlessTableIsExtractedOnlyInsideConfirmedRegion()
    {
        using var document = PdfDocument.Open(BuildPdf(grid: false));
        var tables = TableExtractor().Extract(document, 1, [TableBounds]);
        var table = Assert.Single(tables);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["Name", "Value"], table.Rows[0]);
        Assert.Equal(["Alpha", "10"], table.Rows[1]);
        Assert.DoesNotContain(table.Rows.SelectMany(row => row), cell => cell.Contains("Alice", StringComparison.Ordinal));
    }

    [Fact]
    public void LatticeStillWorksWhenLayoutIsUnavailable()
    {
        using var document = PdfDocument.Open(BuildPdf(grid: true));
        var table = Assert.Single(TableExtractor().Extract(document, 1));
        Assert.Contains("Alpha", table.Rows.SelectMany(row => row));
    }

    [Fact]
    public void BothLatticeAndBorderlessTablesAreProcessedOnOnePage()
    {
        using var document = PdfDocument.Open(BuildPdf(grid: true, secondTable: true));
        var tables = TableExtractor().Extract(document, 1, [TableBounds, new Box(40, 210, 240, 300)]);
        Assert.Equal(2, tables.Count);
        Assert.Contains(tables, table => table.Rows.SelectMany(row => row).Contains("Delta"));
    }

    [Fact]
    public void AcceptedTableRemovesOnlyItsWordsAndPreservesAdjacentText()
    {
        using var document = PdfDocument.Open(BuildPdf(grid: false));
        var tables = TableExtractor().Extract(document, 1, [TableBounds]);
        var text = TextExtractor().Extract(document.GetPage(1), tables.Select(table => table.Box).ToArray(), null);
        var combined = string.Join(" ", text.Select(piece => piece.Text));
        Assert.DoesNotContain("Alpha", combined, StringComparison.Ordinal);
        Assert.Contains("Nearby", combined, StringComparison.Ordinal);
        Assert.Contains("Conclusion", combined, StringComparison.Ordinal);
        Assert.Contains("Alice", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedRegionAndDetectorOmissionsRemainOrdinaryText()
    {
        using var document = PdfDocument.Open(BuildPdf(grid: false));
        var page = document.GetPage(1);
        var text = TextExtractor().Extract(page, [],
            [new PdfLayoutRegion("table", new Box(20, 470, 250, 490)),
                new PdfLayoutRegion("text", new Box(20, 470, 100, 490))]);
        var combined = string.Join(" ", text.Select(piece => piece.Text));
        Assert.Contains("Alice", combined, StringComparison.Ordinal);
        Assert.Contains("Bob", combined, StringComparison.Ordinal);
        Assert.Contains("Alpha", combined, StringComparison.Ordinal);
        Assert.Contains("Conclusion", combined, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("excessive")]
    public async Task LayoutFailureAndCancellationHaveSafeBehavior(string scenario)
    {
        var settings = new FlexyParseOptions();
        settings.ImageLayout.Enabled = scenario != "disabled";
        settings.ImageLayout.MaxRegions = 1;
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var detector = new FakeDetector((_, token) => scenario switch
        {
            "failure" => throw new InvalidOperationException("Test failure"),
            "cancelled" => throw new OperationCanceledException(token),
            "excessive" => [Region("Text", 0, 0, 20, 20), Region("Text", 20, 20, 40, 40)],
            _ => throw new InvalidOperationException("Disabled layout must not call detector")
        });
        var analyzer = new PdfPageLayoutAnalyzer(new PdfImageFallbackRenderer(options), detector, options,
            NullLogger<PdfPageLayoutAnalyzer>.Instance);
        await using var workspace = JobWorkspace.Create(settings.ResolveTempDirectory(), "layout.pdf");
        await File.WriteAllBytesAsync(workspace.SourcePath, BuildPdf(grid: false));
        if (scenario == "cancelled")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(
                workspace.SourcePath, 1, 400, 500, CancellationToken.None).AsTask());
        }
        else
        {
            Assert.Null(await analyzer.AnalyzeAsync(workspace.SourcePath, 1, 400, 500, CancellationToken.None));
            Assert.Equal(scenario == "disabled" ? 0 : 1, detector.Calls);
        }
    }

    [Theory]
    [InlineData("table")]
    [InlineData("empty")]
    [InlineData("failure")]
    public async Task RegisteredPipelineUsesPageDetectorEvenWithoutImageRecognition(string scenario)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:ImageLayout:Enabled"] = "true",
            ["FlexyParse:RecognizeImages"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(configuration);
        var detector = new FakeDetector((bitmap, _) => scenario switch
        {
            "empty" => [],
            "failure" => throw new InvalidOperationException("Test inference failure"),
            _ => [Region("Table", (int)(40d / 400 * bitmap.Width), (int)(100d / 500 * bitmap.Height),
                (int)(240d / 400 * bitmap.Width), (int)(200d / 500 * bitmap.Height))]
        });
        services.AddSingleton<IImageLayoutDetector>(detector);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value;
        await using var workspace = JobWorkspace.Create(options.ResolveTempDirectory(), "layout.pdf");
        await File.WriteAllBytesAsync(workspace.SourcePath, BuildPdf(grid: false));
        await provider.GetRequiredService<DocumentExtractor>().ExtractAsync(workspace, CancellationToken.None);
        var stored = JsonSerializer.Deserialize<StoredPage>(await File.ReadAllTextAsync(workspace.PagePath(1)), JsonDefaults.Options)!;
        Assert.Equal(1, detector.Calls);
        Assert.Equal(scenario == "table" ? 1 : 0, stored.Blocks.OfType<TableBlock>().Count());
        var prose = string.Join(" ", stored.Blocks.OfType<ParagraphBlock>().Select(block => block.Text));
        Assert.Contains("Alice", prose, StringComparison.Ordinal);
        if (scenario == "table") Assert.DoesNotContain("Alpha", prose, StringComparison.Ordinal);
        else Assert.Contains("Alpha", prose, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.Blocks, block => block is ErrorBlock);
        var allText = prose + string.Join(" ", stored.Blocks.OfType<TableBlock>().SelectMany(table => table.Rows).SelectMany(row => row));
        Assert.Equal(1, allText.Split("Alpha", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, allText.Split("Alice", StringSplitOptions.None).Length - 1);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(workspace.SourcePath)!, "page-layout-0001.png")));
    }

    [Fact]
    public async Task OptInDiagnosticsSaveOverlayAndRawDetectionMetadata()
    {
        var settings = new FlexyParseOptions();
        settings.ImageLayout.Enabled = true;
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var detector = new FakeDetector((_, _) => [Region("Table", 20, 20, 200, 200)]);
        var analyzer = new PdfPageLayoutAnalyzer(new PdfImageFallbackRenderer(options), detector, options,
            NullLogger<PdfPageLayoutAnalyzer>.Instance);
        await using var workspace = JobWorkspace.Create(settings.ResolveTempDirectory(), "diagnostics.pdf");
        await File.WriteAllBytesAsync(workspace.SourcePath, BuildPdf(grid: false));
        var directory = Path.GetDirectoryName(workspace.SourcePath)!;
        Assert.NotNull(await analyzer.AnalyzeAsync(workspace.SourcePath, 1, 400, 500, CancellationToken.None, directory));
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "page-layout-0001.json")));
        Assert.Equal("Table", metadata.RootElement.GetProperty("Detections")[0].GetProperty("Label").GetString());
        using var overlay = SKBitmap.Decode(Path.Combine(directory, "page-layout-0001.png"));
        Assert.NotNull(overlay);
        Assert.Equal(metadata.RootElement.GetProperty("Width").GetInt32(), overlay.Width);
    }

    [Fact]
    public void ConfirmedDescriptiveTableIsNotRejectedSolelyByCellLength()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(2000, 500);
        var text = string.Join(" ", Enumerable.Repeat("Descriptive cell content with several words.", 5));
        foreach (var y in new[] { 350, 300 })
        {
            page.AddText(text, 3, new PdfPoint(50, y), font);
            page.AddText(text, 3, new PdfPoint(1050, y), font);
        }

        using var document = PdfDocument.Open(builder.Build());
        var table = Assert.Single(TableExtractor().Extract(document, 1, [new Box(40, 250, 1900, 400)]));
        Assert.All(table.Rows.SelectMany(row => row), cell => Assert.Equal(text, cell));
    }

    [Fact]
    public void LayoutRenderBudgetIsAppliedBeforeBitmapAllocation()
    {
        var renderer = new PdfImageFallbackRenderer(Microsoft.Extensions.Options.Options.Create(new FlexyParseOptions()));
        var (_, width, height) = renderer.GetRenderGeometry(10000, 10000, new Box(0, 0, 10000, 10000), maxPixels: 100);
        Assert.True((long)width * height <= 100);
    }

    [Fact]
    public void CandidateOutsideConfirmedRegionIsRejected()
    {
        Assert.False(TabulaTableExtractor.IsInsideRegion(new Box(0, 0, 200, 200), new Box(0, 0, 10, 10)));
        Assert.True(TabulaTableExtractor.IsInsideRegion(new Box(2, 2, 8, 8), new Box(0, 0, 10, 10)));
    }

    [Fact]
    public void SuppliedPictureTableCompetitionRetainsTableForStructuralValidation()
    {
        var regions = PdfPageLayoutAnalyzer.MapRegions(
            [Region("Picture", 342, 796, 619, 984, 0.50649082660675049),
                Region("Table", 342, 796, 619, 984, 0.25739067792892456),
                Region("Caption", 338, 1009, 628, 1254, 0.39372780919075012),
                Region("Text", 338, 1009, 628, 1254, 0.31970426440238953)],
            1700, 2200, 1700, 2200, 0.25);
        var table = Assert.Single(regions, region => region.Kind == "table");
        Assert.True(table.RequiresStructureValidation);
        Assert.Equal(new Box(342, 1216, 619, 1404), table.Box);
        Assert.Single(regions, region => region.Kind == "figure");
    }

    [Fact]
    public void RuledTableIsRecoveredEvenWhenModelOnlySeesCompositePicture()
    {
        using var document = PdfDocument.Open(BuildPdf(grid: true));
        IReadOnlyList<PdfLayoutRegion> layout = [new("figure", new Box(20, 200, 380, 420))];
        var table = Assert.Single(TableExtractor().ExtractWithLayout(document, 1, layout));
        Assert.Contains("Alpha", table.Rows.SelectMany(row => row));
        Assert.Single(TableExtractor().ExtractWithLayout(document, 1, []));
    }

    [Fact]
    public void AmbiguousBorderlessNumericTableIsRecoveredWithoutPromotingAuthorList()
    {
        using var document = PdfDocument.Open(BuildPdf(grid: false));
        var tables = TableExtractor().ExtractWithLayout(document, 1,
            [new("figure", TableBounds), new("table", TableBounds, RequiresStructureValidation: true),
                new("table", new Box(10, 450, 250, 495), RequiresStructureValidation: true)]);
        var table = Assert.Single(tables);
        Assert.Contains("Alpha", table.Rows.SelectMany(row => row));
        Assert.DoesNotContain("Alice", table.Rows.SelectMany(row => row));
    }

    [Fact]
    public void WeakTableHypothesisRequiresRepeatedDataNotMerelyAlignment()
    {
        Assert.False(TabulaTableExtractor.HasTabularData(
            [new[] { "Alice", "Bob" }, new[] { "IBM Research", "University" }, new[] { "Switzerland", "USA" }]));
        Assert.False(TabulaTableExtractor.HasTabularData(
            [new[] { "Name", "Count" }, new[] { "A", "1" }]));
        Assert.True(TabulaTableExtractor.HasTabularData(
            [new[] { "Class", "AP" }, new[] { "Text", "87.5" }, new[] { "Table", "88.1 ± 0.2" }]));
    }

    [Fact]
    public void CollapsedLatticeRowsFallBackToStreamWithinTableRegion()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(400, 500);
        var labels = new[] { "Name", "Alpha", "Beta", "Gamma" };
        var values = new[] { "Value", "10", "20", "30" };
        var positions = new[] { 380, 355, 335, 315 };
        for (var index = 0; index < labels.Length; index++)
        {
            page.AddText(labels[index], 10, new PdfPoint(50, positions[index]), font);
            page.AddText(values[index], 10, new PdfPoint(150, positions[index]), font);
        }

        foreach (var x in new[] { 40, 140, 240 })
            page.DrawLine(new PdfPoint(x, 300), new PdfPoint(x, 400));
        // A frame plus a header rule is not enough evidence to merge all remaining records.
        foreach (var y in new[] { 300, 365, 400 })
            page.DrawLine(new PdfPoint(40, y), new PdfPoint(240, y));
        using var document = PdfDocument.Open(builder.Build());
        var table = Assert.Single(TableExtractor().Extract(document, 1, [TableBounds]));
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(["Beta", "20"], table.Rows[2]);
    }

    [Fact]
    public void CollapsedGridCheckDistinguishesNumericStacksFromWrappedDescriptions()
    {
        Assert.True(TabulaTableExtractor.HasCollapsedGridRow(["Alpha\rBeta\rGamma", "10\r20\r30"]));
        Assert.False(TabulaTableExtractor.HasCollapsedGridRow(["A long\rwrapped\rdescription", "Another\rwrapped\rdescription"]));
        Assert.False(TabulaTableExtractor.HasCollapsedGridRow(["Name", "10\r20\r30"]));
    }

    [Theory]
    [InlineData("nested-table")]
    [InlineData("weak-table")]
    [InlineData("nested-failure-grid")]
    public async Task CompositeAndAmbiguousTablesReachMarkdownWithoutDuplicatingText(string scenario)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:ImageLayout:Enabled"] = "true",
            // This scenario deliberately exercises a weak, but still eligible, table prediction.
            ["FlexyParse:ImageLayout:Confidence"] = "0.25",
            ["FlexyParse:RecognizeImages"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(configuration);
        var calls = 0;
        var figureBox = new Box(30, 200, 250, 410);
        var detector = new FakeDetector((bitmap, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return scenario == "weak-table"
                    ? [RegionOnBitmap("Picture", TableBounds, bitmap, 400, 500, 0.50649082660675049),
                        RegionOnBitmap("Table", TableBounds, bitmap, 400, 500, 0.25739067792892456)]
                    : [RegionOnBitmap("Picture", figureBox, bitmap, 400, 500, 0.95568835735321045)];
            }

            if (scenario == "nested-failure-grid") throw new InvalidOperationException("Test figure inference failure");
            if (scenario == "weak-table") return [];
            return [RegionOnBitmap("Table", new Box(10, 100, 210, 200), bitmap, figureBox.Width, figureBox.Height)];
        });
        services.AddSingleton<IImageLayoutDetector>(detector);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value;
        await using var workspace = JobWorkspace.Create(options.ResolveTempDirectory(), "composite.pdf");
        await File.WriteAllBytesAsync(workspace.SourcePath, BuildPdf(grid: scenario == "nested-failure-grid"));
        var index = await provider.GetRequiredService<DocumentExtractor>().ExtractAsync(workspace, CancellationToken.None);
        using var output = new MemoryStream();
        await provider.GetRequiredService<MarkdownDocumentWriter>().WriteAsync(workspace, index, output, CancellationToken.None);
        var markdown = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("|", markdown, StringComparison.Ordinal);
        Assert.Contains("Alpha", markdown, StringComparison.Ordinal);
        Assert.Equal(1, markdown.Split("Alpha", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, markdown.Split("Alice", StringSplitOptions.None).Length - 1);
        var stored = JsonSerializer.Deserialize<StoredPage>(await File.ReadAllTextAsync(workspace.PagePath(1)), JsonDefaults.Options)!;
        Assert.Single(stored.Blocks.OfType<TableBlock>());
        Assert.DoesNotContain(stored.Blocks.OfType<ParagraphBlock>(), block => block.Text.Contains("Alpha", StringComparison.Ordinal));
        Assert.Equal(2, detector.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FigurePassesAreBoundedAndNeverRecurse(int limit)
    {
        var settings = new FlexyParseOptions();
        settings.ImageLayout.Enabled = true;
        settings.ImageLayout.MaxFigurePasses = limit;
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var detector = new FakeDetector((bitmap, _) => [Region("Picture", 0, 0, bitmap.Width, bitmap.Height)]);
        var analyzer = new PdfPageLayoutAnalyzer(new PdfImageFallbackRenderer(options), detector, options,
            NullLogger<PdfPageLayoutAnalyzer>.Instance);
        await using var workspace = JobWorkspace.Create(settings.ResolveTempDirectory(), "bounded.pdf");
        await File.WriteAllBytesAsync(workspace.SourcePath, BuildPdf(grid: false));
        Assert.NotNull(await analyzer.AnalyzeAsync(workspace.SourcePath, 1, 400, 500, CancellationToken.None));
        Assert.Equal(1 + limit, detector.Calls);
    }

    [Fact]
    public async Task CancellationDuringFigureDetectionPropagates()
    {
        var settings = new FlexyParseOptions();
        settings.ImageLayout.Enabled = true;
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var calls = 0;
        var detector = new FakeDetector((bitmap, token) => ++calls == 1
            ? [Region("Picture", 0, 0, bitmap.Width, bitmap.Height)] : throw new OperationCanceledException(token));
        var analyzer = new PdfPageLayoutAnalyzer(new PdfImageFallbackRenderer(options), detector, options,
            NullLogger<PdfPageLayoutAnalyzer>.Instance);
        await using var workspace = JobWorkspace.Create(settings.ResolveTempDirectory(), "cancelled.pdf");
        await File.WriteAllBytesAsync(workspace.SourcePath, BuildPdf(grid: false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(
            workspace.SourcePath, 1, 400, 500, CancellationToken.None).AsTask());
        Assert.Equal(2, detector.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(17)]
    public void RejectsInvalidFigurePassLimits(int limit)
    {
        var settings = new ImageLayoutOptions { Enabled = true, MaxFigurePasses = limit };
        Assert.False(settings.IsValid());
    }

    private static ImageLayoutRegion RegionOnBitmap(string label, Box box, SKBitmap image, double width, double height,
        double score = 0.9) => Region(label, (int)Math.Floor(box.Left / width * image.Width),
            (int)Math.Floor((height - box.Top) / height * image.Height),
            (int)Math.Ceiling(box.Right / width * image.Width),
            (int)Math.Ceiling((height - box.Bottom) / height * image.Height), score);

    private static readonly Box TableBounds = new(40, 300, 240, 400);

    private static byte[] BuildPdf(bool grid, bool secondTable = false)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(400, 500);
        page.AddText("Alice", 10, new PdfPoint(20, 480), font);
        page.AddText("Bob", 10, new PdfPoint(160, 480), font);
        page.AddText("Institute One", 10, new PdfPoint(20, 465), font);
        page.AddText("Institute Two", 10, new PdfPoint(160, 465), font);
        page.AddText("Name", 10, new PdfPoint(50, 380), font);
        page.AddText("Value", 10, new PdfPoint(150, 380), font);
        page.AddText("Alpha", 10, new PdfPoint(50, 350), font);
        page.AddText("10", 10, new PdfPoint(150, 350), font);
        page.AddText("Beta", 10, new PdfPoint(50, 320), font);
        page.AddText("20", 10, new PdfPoint(150, 320), font);
        page.AddText("Nearby prose", 10, new PdfPoint(260, 350), font);
        page.AddText("Conclusion", 10, new PdfPoint(40, 180), font);
        if (grid)
        {
            foreach (var x in new[] { 40, 140, 240 })
                page.DrawLine(new PdfPoint(x, 300), new PdfPoint(x, 400));
            foreach (var y in new[] { 300, 335, 365, 400 })
                page.DrawLine(new PdfPoint(40, y), new PdfPoint(240, y));
        }

        if (secondTable)
        {
            page.AddText("Item", 10, new PdfPoint(50, 280), font);
            page.AddText("Count", 10, new PdfPoint(150, 280), font);
            page.AddText("Delta", 10, new PdfPoint(50, 250), font);
            page.AddText("30", 10, new PdfPoint(150, 250), font);
            page.AddText("Gamma", 10, new PdfPoint(50, 220), font);
            page.AddText("40", 10, new PdfPoint(150, 220), font);
        }

        return builder.Build();
    }

    private static ImageLayoutRegion Region(string label, int left, int top, int right, int bottom, double score = 0.9)
        => new(label, new SKRectI(left, top, right, bottom), score);

    private static TabulaTableExtractor TableExtractor() => new(NullLogger<TabulaTableExtractor>.Instance);
    private static PdfTextExtractor TextExtractor() => new(NullLogger<PdfTextExtractor>.Instance);

    private sealed class FakeDetector(Func<SKBitmap, CancellationToken, IReadOnlyList<ImageLayoutRegion>> detect) : IImageLayoutDetector
    {
        public int Calls { get; private set; }
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<ImageLayoutRegion>> DetectAsync(SKBitmap image, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(detect(image, cancellationToken));
        }
    }
}
