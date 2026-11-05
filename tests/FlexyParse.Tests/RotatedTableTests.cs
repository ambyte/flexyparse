using System.Text;
using System.Text.Json;
using FlexyParse.DependencyInjection;
using FlexyParse.Extraction;
using FlexyParse.Model;
using FlexyParse.Options;
using FlexyParse.Output;
using FlexyParse.Serialization;
using FlexyParse.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Writer;

namespace FlexyParse.Tests;

public class RotatedTableTests
{
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(-1, 1, false)]
    [InlineData(1, 0.03, true)]
    [InlineData(-1, 0.03, true)]
    public void ReadsRotatedHeadersAndPreservesSparseData(int direction, double scale, bool doubleBorders)
    {
        using var document = PdfDocument.Open(BuildTable(direction, scale, doubleBorders));
        var table = Assert.Single(Extractor().Extract(document, 1));

        Assert.Equal(4, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(4, row.Count));
        Assert.Equal(["Element", "LOD: Nominal size", "LOD: Exact position", "LOI: Material"], table.Rows[0]);
        Assert.Equal(["Walls", "", "+", "+"], table.Rows[1]);
        Assert.Equal(["Decorative elements", "+", "", ""], table.Rows[2]);
        Assert.Equal(["Rooms", "", "", "+"], table.Rows[3]);

        var text = new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance)
            .Extract(document.GetPage(1), [table.Box], null);
        var prose = string.Join(" ", text.Select(piece => piece.Text));
        Assert.Contains("Table caption", prose, StringComparison.Ordinal);
        Assert.Contains("Footnote", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("Walls", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("Nominal", prose, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedLayoutDoesNotAddASecondStreamCopy()
    {
        using var document = PdfDocument.Open(BuildTable(1, 1, true));
        var table = Assert.Single(Extractor().Extract(document, 1, [new Box(39, 99, 381, 401)]));
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(1, table.Rows.SelectMany(row => row).Count(cell => cell == "Walls"));
    }

    [Fact]
    public void PartialHeaderRuleDoesNotMergeFirstDataRowIntoHeader()
    {
        using var document = PdfDocument.Open(BuildTable(1, 1, false, partialHeaderRule: true));
        var table = Assert.Single(Extractor().Extract(document, 1));
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal("Element", table.Rows[0][0]);
        Assert.Equal(["Walls", "", "+", "+"], table.Rows[1]);
    }

    [Fact]
    public async Task RecoveredTableReachesMarkdownAndStoredJsonWithoutDuplicatingProse()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlexyParse:ImageLayout:Enabled"] = "false",
            ["FlexyParse:RecognizeImages"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexyParse(configuration);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FlexyParseOptions>>().Value;
        await using var workspace = JobWorkspace.Create(options.ResolveTempDirectory(), "rotated.pdf");
        await File.WriteAllBytesAsync(workspace.SourcePath, BuildTable(1, 0.03, true));
        var index = await provider.GetRequiredService<DocumentExtractor>().ExtractAsync(workspace, CancellationToken.None);
        using var output = new MemoryStream();
        await provider.GetRequiredService<MarkdownDocumentWriter>().WriteAsync(workspace, index, output, CancellationToken.None);
        var markdown = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("LOD: Nominal size", markdown, StringComparison.Ordinal);
        Assert.Contains("LOD: Exact position", markdown, StringComparison.Ordinal);
        Assert.Equal(1, markdown.Split("Walls", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, markdown.Split("Table caption", StringSplitOptions.None).Length - 1);
        var stored = JsonSerializer.Deserialize<StoredPage>(await File.ReadAllTextAsync(workspace.PagePath(1)), JsonDefaults.Options)!;
        var table = Assert.Single(stored.Blocks.OfType<TableBlock>());
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(["Rooms", "", "", "+"], table.Rows[3]);
        Assert.DoesNotContain(stored.Blocks.OfType<ParagraphBlock>(), block => block.Text.Contains("Walls", StringComparison.Ordinal));
    }

    [Fact]
    public void VerticalProseWithoutGridIsNotPromotedToTable()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(420, 500);
        AddRotated(page, font, "Vertical prose", 100, 200, 1, 1);
        page.AddText("Ordinary paragraph", 10, new PdfPoint(40, 100), font);
        using var document = PdfDocument.Open(builder.Build());
        Assert.Empty(Extractor().Extract(document, 1));
    }

    [Fact]
    public void RotatedBodyCellIsNotFlattenedIntoTheHeader()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(420, 500);
        foreach (var x in new[] { 40, 170, 300 })
            page.DrawLine(new PdfPoint(x, 100), new PdfPoint(x, 400));
        foreach (var y in new[] { 100, 150, 350, 400 })
            page.DrawLine(new PdfPoint(40, y), new PdfPoint(300, y));
        page.AddText("Name", 10, new PdfPoint(50, 380), font);
        page.AddText("Value", 10, new PdfPoint(180, 380), font);
        page.AddText("Walls", 10, new PdfPoint(50, 260), font);
        AddRotated(page, font, "Data", 205, 220, 1, 1);
        page.AddText("Rooms", 10, new PdfPoint(50, 125), font);
        page.AddText("10", 10, new PdfPoint(180, 125), font);
        using var document = PdfDocument.Open(builder.Build());
        var table = Assert.Single(Extractor().Extract(document, 1));
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["Name", "Value"], table.Rows[0]);
        Assert.Equal(["Walls", "Data"], table.Rows[1]);
        Assert.Equal(["Rooms", "10"], table.Rows[2]);
    }

    [Fact]
    public void ExtractionPropagatesCancellation()
    {
        using var document = PdfDocument.Open(BuildTable(1, 1, true));
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Extractor().Extract(document, 1, source.Token));
    }

    private static TabulaTableExtractor Extractor() => new(NullLogger<TabulaTableExtractor>.Instance);

    private static byte[] BuildTable(int direction, double scale, bool doubleBorders, bool partialHeaderRule = false)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(420, 500);
        page.AddText("Table caption", 10, new PdfPoint(40, 430), font);
        page.AddText("Footnote", 10, new PdfPoint(40, 70), font);
        page.AddText("Element", 10, new PdfPoint(60, 330), font);
        page.AddText("LOD", 10, new PdfPoint(225, 382), font);
        page.AddText("LOI", 10, new PdfPoint(335, 382), font);
        AddRotated(page, font, "Nominal size", direction > 0 ? 205 : 195, direction > 0 ? 280 : 360, direction, scale);
        AddRotated(page, font, "Exact", direction > 0 ? 258 : 282, direction > 0 ? 300 : 350, direction, scale);
        AddRotated(page, font, "position", direction > 0 ? 273 : 267, direction > 0 ? 290 : 360, direction, scale);
        AddRotated(page, font, "Material", direction > 0 ? 345 : 335, direction > 0 ? 290 : 360, direction, scale);
        page.AddText("Walls", 10, new PdfPoint(50, 230), font);
        AddRotated(page, font, " ", 205, 230, direction, scale);
        page.AddText("+", 10, new PdfPoint(275, 230), font);
        page.AddText("+", 10, new PdfPoint(345, 230), font);
        page.AddText("Decorative", 10, new PdfPoint(50, 185), font);
        page.AddText("elements", 10, new PdfPoint(50, 170), font);
        page.AddText("+", 10, new PdfPoint(205, 180), font);
        page.AddText("Rooms", 10, new PdfPoint(50, 125), font);
        page.AddText("+", 10, new PdfPoint(345, 125), font);

        foreach (var offset in doubleBorders ? new[] { 0d, 1.5 } : new[] { 0d })
        {
            foreach (var x in new[] { 40, 170, 310, 380 })
                page.DrawLine(new PdfPoint(x + offset, 100), new PdfPoint(x + offset, 400));
            page.DrawLine(new PdfPoint(240 + offset, 100), new PdfPoint(240 + offset, 375));
            foreach (var y in new[] { 100, 150, 200, 250, 400 })
                page.DrawLine(new PdfPoint(partialHeaderRule && y == 250 ? 170 : 40, y + offset), new PdfPoint(380, y + offset));
            page.DrawLine(new PdfPoint(170, 375 + offset), new PdfPoint(380, 375 + offset));
            if (partialHeaderRule)
                page.DrawLine(new PdfPoint(40, 250 + offset), new PdfPoint(100, 250 + offset));
        }

        // Inset decorative outlines must not create blank 3-point columns or split wrapped text.
        page.DrawRectangle(new PdfPoint(43.5, 100), 123, 150);
        page.DrawRectangle(new PdfPoint(245, 280), 30, 70);
        return builder.Build();
    }

    private static void AddRotated(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font,
        string text, double x, double y, int direction, double scale)
    {
        var operations = page.ContentStreams[0].Operations;
        operations.Add(Push.Value);
        operations.Add(new ModifyCurrentTransformationMatrix([0, direction * scale, -direction * scale, 0, x, y]));
        page.AddText(text, 10 / scale, new PdfPoint(0, 0), font);
        operations.Add(Pop.Value);
    }
}
