using FlexyParse.Extraction;
using FlexyParse.Model;

namespace FlexyParse.Tests;

public class PageContentComposerTests
{
    [Fact]
    public void DropsTextCoveredByTableAndKeepsHeading()
    {
        var heading = new TextPiece
        {
            Box = new Box(40, 760, 200, 790),
            Text = "Report",
            AverageFontSize = 22,
            LineCount = 1
        };
        var body = new TextPiece
        {
            Box = new Box(40, 400, 400, 460),
            Text = "A paragraph that stays beside the table.",
            AverageFontSize = 11,
            LineCount = 2
        };
        var insideTable = new TextPiece
        {
            Box = new Box(50, 120, 180, 150),
            Text = "cell text",
            AverageFontSize = 11,
            LineCount = 1
        };
        var table = new TablePiece
        {
            Box = new Box(40, 100, 400, 200),
            Rows =
            [
                new[] { "Name", "Value" },
                new[] { "A", "1" }
            ]
        };

        var blocks = PageContentComposer.Compose([heading, body, insideTable], [table], []);

        Assert.Equal(3, blocks.Count);
        Assert.IsType<HeadingBlock>(blocks[0]);
        Assert.Equal("Report", ((HeadingBlock)blocks[0]).Text);
        Assert.IsType<ParagraphBlock>(blocks[1]);
        Assert.IsType<TableBlock>(blocks[2]);
    }

    [Fact]
    public void ReadsColumnsWithoutInterleavingAndKeepsSpanningBands()
    {
        var pieces = new[]
        {
            Piece("Title", 20, 760, 580, 790),
            Piece("Left first", 20, 650, 270, 730),
            Piece("Right first", 320, 660, 580, 735),
            Piece("Left second", 20, 500, 270, 620),
            Piece("Right second", 320, 510, 580, 630),
            Piece("Full width section", 20, 450, 580, 475),
            Piece("Lower left", 20, 200, 270, 420),
            Piece("Lower right", 320, 200, 580, 420)
        };
        var blocks = PageContentComposer.Compose(pieces, [], []);
        Assert.Equal(["Title", "Left first", "Left second", "Right first", "Right second", "Full width section", "Lower left", "Lower right"],
            blocks.Cast<ParagraphBlock>().Select(block => block.Text));
    }

    [Fact]
    public void PreFilteredTextIsNotDroppedByLargeParagraphOverlap()
    {
        var text = Piece("Text outside the table has already been retained word by word", 20, 100, 300, 300);
        var table = new TablePiece { Box = new Box(20, 100, 300, 250), Rows = [new[] { "A", "B" }, new[] { "1", "2" }] };
        var blocks = PageContentComposer.Compose([text], [table], [], textExcludesTables: true);
        Assert.Single(blocks.OfType<ParagraphBlock>());
        Assert.Single(blocks.OfType<TableBlock>());
    }

    private static TextPiece Piece(string text, double left, double bottom, double right, double top) => new()
    {
        Box = new Box(left, bottom, right, top), Text = text, AverageFontSize = 11, LineCount = 2
    };
}
