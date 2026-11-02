using FlexyParse.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FlexyParse.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData(" \r\n\t ", "")]
    [InlineData("First line\ncontinuation of paragraph", "First line continuation of paragraph")]
    [InlineData("  First  line\r\n second\rthird\n", "First line second third")]
    [InlineData("\nFirst\nparagraph\n \n\nSecond\nparagraph\n", "First paragraph\n\nSecond paragraph")]
    [InlineData("Text\n• First item\ncontinued\n• Second item", "Text\n• First item continued\n• Second item")]
    [InlineData("- First\ncontinued\n- Second", "- First continued\n- Second")]
    [InlineData("* First\n+ Second\n▪ Third\n◦ Fourth\n– Fifth\n— Sixth", "* First\n+ Second\n▪ Third\n◦ Fourth\n– Fifth\n— Sixth")]
    [InlineData("1. First\ncontinued\n2. Second\n10) Third", "1. First continued\n2. Second\n10) Third")]
    [InlineData("Value\n1.25 units\n2026 report", "Value 1.25 units 2026 report")]
    [InlineData("First\n\n- Item\ncontinued", "First\n\n- Item continued")]
    [InlineData("A\u00a0B\nＣ", "A B C")]
    [InlineData("well-\nknown", "well- known")]
    public void NormalizesPdfWrappingWithoutRemovingParagraphOrListBoundaries(string? source, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeBlock(source));
    }

    [Fact]
    public void CellNormalizationStillCollapsesAllWhitespace()
    {
        Assert.Equal("First second third", TextNormalizer.NormalizeCell("First\n\nsecond\r\nthird"));
    }

    [Fact]
    public void ExtractorJoinsWrappedPdfLinesButRetainsPhysicalLineCount()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText("This paragraph starts on the first line", 12, new PdfPoint(72, 760), font);
        page.AddText("and continues on the second line.", 12, new PdfPoint(72, 744), font);
        using var document = PdfDocument.Open(builder.Build());
        var extractor = new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance);

        var piece = Assert.Single(extractor.Extract(document.GetPage(1)));

        Assert.Equal("This paragraph starts on the first line and continues on the second line.", piece.Text);
        Assert.Equal(2, piece.LineCount);
    }
}
