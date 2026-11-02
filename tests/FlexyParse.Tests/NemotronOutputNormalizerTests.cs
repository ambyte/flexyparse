using System.Text.Json;
using FlexyParse.Markdown;
using FlexyParse.Nemotron;

namespace FlexyParse.Tests;

public sealed class NemotronOutputNormalizerTests
{
    [Fact]
    public void LayoutMarkedTablesBecomeSeparateStructuredBlocks()
    {
        const string source = """
            <x_0.0488><y_0.0391>## A

            \begin{tabular}{ccc}
            **Class label** & **Count** & **Train**\\
            Caption & 22545 & 2.06\\
            Total & 104704 & 40.92\\
            \end{tabular}<x_0.4932><y_0.357><class_Table>

            <x_0.5166><y_0.0195>## B

            \begin{tabular}{cc}
            Turn & Count & Extra\\
            Common & 10\\
            \end{tabular}<x_0.959><y_0.5734><class_Table>
            """;
        var normalized = NemotronOutputNormalizer.Normalize(source);
        using var blocks = Parse(normalized);
        Assert.Equal(4, blocks.RootElement.GetArrayLength());
        Assert.Equal("heading", blocks.RootElement[0].GetProperty("type").GetString());
        Assert.Equal("A", blocks.RootElement[0].GetProperty("text").GetString());
        Assert.Equal("table", blocks.RootElement[1].GetProperty("type").GetString());
        Assert.Equal("22545", blocks.RootElement[1].GetProperty("rows")[1][1].GetString());
        Assert.Equal("table", blocks.RootElement[3].GetProperty("type").GetString());
        Assert.Equal("Extra", blocks.RootElement[3].GetProperty("rows")[0][2].GetString());
        Assert.Equal("", blocks.RootElement[3].GetProperty("rows")[1][2].GetString());
        Assert.DoesNotContain("<x_", normalized);
        Assert.DoesNotContain("<y_", normalized);
        Assert.DoesNotContain("<class_", normalized);
        Assert.DoesNotContain(@"\begin{tabular}", normalized);
    }

    [Fact]
    public void SpanningHeadersAndRulesBecomeRectangularRowsWithoutLosingValues()
    {
        const string source = """
            \begin{tabular}[t]{|l|*{2}{c}|}
            \toprule
            \multirow{2}{*}{Label} & \multicolumn{2}{c}{Scores}\\[2pt]
            \cline{2-3}
             & Train & Test\\
            \midrule
            \textbf{Caption} & 2 & 3\\
            \bottomrule
            \end{tabular}
            """;
        using var blocks = Parse(NemotronOutputNormalizer.Normalize(source));
        var rows = blocks.RootElement[0].GetProperty("rows");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("Label", rows[0][0].GetString());
        Assert.Equal("Scores", rows[0][1].GetString());
        Assert.Equal("", rows[0][2].GetString());
        Assert.Equal("Train", rows[1][1].GetString());
        Assert.Equal("Test", rows[1][2].GetString());
        Assert.Equal("**Caption**", rows[2][0].GetString());
        Assert.Equal("3", rows[2][2].GetString());
    }

    [Fact]
    public void EscapedSeparatorsNestedGroupsAndFormulasRemainInTheirCells()
    {
        const string source = """
            \begin{tabular}{ccc}
            Name & Value & Formula\\
            R\&D & 50\% | 2 & $x_{a&b}$\\
            Empty & & Last
            \end{tabular}
            """;
        using var blocks = Parse(NemotronOutputNormalizer.Normalize(source));
        var rows = blocks.RootElement[0].GetProperty("rows");
        Assert.Equal(3, rows[1].GetArrayLength());
        Assert.Equal("R&D", rows[1][0].GetString());
        Assert.Contains("50%", rows[1][1].GetString());
        Assert.Contains("2", rows[1][1].GetString());
        Assert.Equal("$x_{a&b}$", rows[1][2].GetString());
        Assert.Equal("", rows[2][1].GetString());
        Assert.Equal("Last", rows[2][2].GetString());
    }

    [Theory]
    [InlineData(@"\begin{tabular}{cc} A & B\\ 1 & {bad \end{tabular}")]
    [InlineData(@"\begin{tabular}{cc} \multicolumn{999999999}{c}{A} & B\\ \end{tabular}")]
    [InlineData(@"\begin{tabular}{cc} A & B\\")]
    public void MalformedOrIncompleteTablesAreRetainedRatherThanDiscarded(string source)
    {
        Assert.Equal(source, NemotronOutputNormalizer.Normalize(source));
    }

    [Theory]
    [InlineData("```")]
    [InlineData("~~~")]
    public void FencedExamplesAreNotInterpreted(string fence)
    {
        var source = fence + "latex\n<x_0.1>\n" + @"\begin{tabular}{c}A\\\end{tabular}" + "\n" + fence;
        Assert.Equal(source, NemotronOutputNormalizer.Normalize(source));
    }

    [Fact]
    public void OrdinaryMarkdownHtmlAndMathAreUnchanged()
    {
        const string source = "# Heading\n\n**Text** <span>value</span> $x_1$\n\n| A | B |\n|---|---|\n|1|2|";
        Assert.Equal(source, NemotronOutputNormalizer.Normalize(source));
    }

    [Theory]
    [InlineData("`<x_0.1>` and `<class_Table>`")]
    [InlineData("``literal ` <no_bbox>``")]
    [InlineData("`\\begin{tabular}{c}A\\\\\\end{tabular}`")]
    [InlineData("    <x_0.1>\n    \\begin{tabular}{c}A\\\\\\end{tabular}")]
    [InlineData("````latex\n```\n<x_0.1>\n\\begin{tabular}{c}A\\\\\\end{tabular}\n````")]
    [InlineData("~~~~latex\n~~~\n<x_0.1>\n\\begin{tabular}{c}A\\\\\\end{tabular}\n~~~~")]
    [InlineData("```latex\n<x_0.1>\n\\begin{tabular}{c}A\\\\\\end{tabular}")]
    [InlineData("> ```latex\n> <x_0.1>\n> ```")]
    [InlineData("~~~latex\n<x_0.1>\n\\begin{tabular}{c}A\\\\\\end{tabular}")]
    public void MarkdownCodeIsPreserved(string source)
    {
        Assert.Equal(source, NemotronOutputNormalizer.Normalize(source));
    }

    [Fact]
    public void UnclosedFenceInsideQuoteDoesNotProtectFollowingProse()
    {
        const string source = "> ```latex\n> <x_0.1>\n\n<no_bbox>Text";
        Assert.Equal("> ```latex\n> <x_0.1>\n\nText", NemotronOutputNormalizer.Normalize(source));
    }

    [Fact]
    public void MixedMarkdownAndLatexKeepTheirStructureAndCodeExamples()
    {
        const string source = """
            <output_markdown><no_bbox>## Results

            **Summary** with $x_1$ and `<class_Table>`.

            - First item
            - Second item

            | Name | Value |
            | --- | --- |
            | Markdown | 1 |

            \begin{tabular}{cc}
            Name & Value\\
            LaTeX & 2\\
            \end{tabular}<class_Table>

            ````latex
            ```
            <x_0.1>\begin{tabular}{c}Example\\\end{tabular}
            ````
            """;
        var normalized = NemotronOutputNormalizer.Normalize(source);
        using var blocks = Parse(normalized);
        Assert.Equal(6, blocks.RootElement.GetArrayLength());
        Assert.Equal("heading", blocks.RootElement[0].GetProperty("type").GetString());
        Assert.Equal("paragraph", blocks.RootElement[1].GetProperty("type").GetString());
        Assert.Equal("list", blocks.RootElement[2].GetProperty("type").GetString());
        Assert.Equal("Markdown", blocks.RootElement[3].GetProperty("rows")[1][0].GetString());
        Assert.Equal("LaTeX", blocks.RootElement[4].GetProperty("rows")[1][0].GetString());
        Assert.Contains("`<class_Table>`", normalized);
        Assert.Contains(@"<x_0.1>\begin{tabular}{c}Example\\\end{tabular}", normalized);
        Assert.DoesNotContain("<output_markdown>", normalized);
        Assert.DoesNotContain("<no_bbox>", normalized);
        Assert.Equal(normalized, NemotronOutputNormalizer.Normalize(normalized));
    }

    [Fact]
    public void NormalizationIsIdempotentAndHonorsCancellation()
    {
        var normalized = NemotronOutputNormalizer.Normalize(@"<no_bbox>\begin{tabular}{c}A\\1\\\end{tabular}<class_Table>");
        Assert.Equal(normalized, NemotronOutputNormalizer.Normalize(normalized));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => NemotronOutputNormalizer.Normalize(normalized, cancellation.Token));
    }

    private static JsonDocument Parse(string markdown)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            new MarkdownJsonParser().Write(writer, markdown);
        return JsonDocument.Parse(stream.ToArray());
    }
}
