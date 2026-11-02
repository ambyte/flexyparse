using System.Text.Json;
using FlexyParse.Markdown;
using Markdig;
using TagBites.Text.Markdown;

namespace FlexyParse.Tests;

public class MarkdownJsonParserTests
{
    [Fact]
    public void PreservesHeadingsFormattingLinksImagesAndEntities()
    {
        using var document = Parse("# Headline\n\nText **bold** *italic* ~~old~~ `code` &amp; [link](https://example.com \"Title\") ![alt](image.png)");
        var blocks = document.RootElement;
        Assert.Equal("heading", Type(blocks[0]));
        Assert.Equal(1, blocks[0].GetProperty("level").GetInt32());
        Assert.Equal("Headline", blocks[0].GetProperty("text").GetString());
        Assert.Equal("Text **bold** *italic* ~~old~~ `code` &amp; [link](https://example.com \"Title\") ![alt](image.png)",
            blocks[1].GetProperty("text").GetString());
        Assert.Equal(2, blocks[1].EnumerateObject().Count());
    }

    [Fact]
    public void PreservesNestedListsQuotesAndTasks()
    {
        using var document = Parse("3. First\n   - Nested\n\n> Quote\n\n- [x] Done\n- [ ] Pending");
        var blocks = document.RootElement;
        Assert.Equal("list", Type(blocks[0]));
        Assert.True(blocks[0].GetProperty("ordered").GetBoolean());
        Assert.Equal("3", blocks[0].GetProperty("start").GetString());
        var item = blocks[0].GetProperty("items")[0];
        Assert.Equal("First", item[0].GetProperty("text").GetString());
        Assert.Equal("list", Type(item[1]));
        Assert.Equal("quote", Type(blocks[1]));
        Assert.Equal("Quote", blocks[1].GetProperty("blocks")[0].GetProperty("text").GetString());
        var tasks = blocks[2].GetProperty("items");
        Assert.Equal("[x] Done", tasks[0][0].GetProperty("text").GetString());
        Assert.Equal("[ ] Pending", tasks[1][0].GetProperty("text").GetString());
        Assert.False(blocks[2].TryGetProperty("ordered", out _));
    }

    [Fact]
    public void PreservesTableHeadersAndCellContents()
    {
        using var document = Parse("| Name | Qty |\n| :--- | ---: |\n| **A** | 2 |");
        var table = document.RootElement[0];
        Assert.Equal("table", Type(table));
        Assert.Equal("left", table.GetProperty("alignments")[0].GetString());
        Assert.Equal("right", table.GetProperty("alignments")[1].GetString());
        var rows = table.GetProperty("rows");
        Assert.Equal("Name", rows[0][0].GetString());
        Assert.Equal("Qty", rows[0][1].GetString());
        Assert.Equal("**A**", rows[1][0].GetString());
        Assert.Equal("2", rows[1][1].GetString());
        Assert.False(table.TryGetProperty("children", out _));
    }

    [Fact]
    public void EscapesCodeAndPreservesMathAndHtmlAsData()
    {
        using var document = Parse("```csharp\nvar path = \"C:\\temp\";\n// \"quoted\"\n```\n\nFormula $E=mc^2$.\n\n$$\nx^2\n$$\n\n<script>alert('x')</script>\n\n---");
        var blocks = document.RootElement;
        Assert.Equal("code", Type(blocks[0]));
        Assert.Equal("csharp", blocks[0].GetProperty("language").GetString());
        Assert.Contains("C:\\temp", blocks[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("Formula $E=mc^2$.", blocks[1].GetProperty("text").GetString());
        Assert.Equal("paragraph", Type(blocks[2]));
        Assert.Contains("x^2", blocks[2].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("markdown", Type(blocks[3]));
        Assert.Contains("<script>", blocks[3].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("thematicBreak", Type(blocks[4]));
        Assert.Single(blocks[4].EnumerateObject());
    }

    [Fact]
    public void ReplacesFormUnderlinesWithoutRemovingEmphasis()
    {
        using var document = Parse("Name **John Doe** " + new string('_', 16) + "\n\nUse `________` and C++ and $E=mc^2$.");
        var blocks = document.RootElement;
        Assert.Equal("paragraph", Type(blocks[0]));
        Assert.Equal("Name **John Doe** " + new string(MarkdownDelimiterSanitizer.Line, 16), blocks[0].GetProperty("text").GetString());
        Assert.Contains("`________`", blocks[1].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("C++", blocks[1].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("$E=mc^2$.", blocks[1].GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsThematicBreaksSetextHeadingsCodeFencesAndGridRules()
    {
        using var document = Parse("Title\n========\n\n" + new string('_', 12) + "\n\n```text\n________\n```\n\n+------+------+\n| A    | B    |\n+======+======+\n| 1    | 2    |\n+------+------+");
        var blocks = document.RootElement;
        Assert.Equal("heading", Type(blocks[0]));
        Assert.Equal("Title", blocks[0].GetProperty("text").GetString());
        Assert.Equal("thematicBreak", Type(blocks[1]));
        Assert.Equal("code", Type(blocks[2]));
        Assert.Contains("________", blocks[2].GetProperty("text").GetString(), StringComparison.Ordinal);
        var table = blocks[3];
        Assert.Equal("table", Type(table));
        Assert.Equal("A", table.GetProperty("rows")[0][0].GetString());
        Assert.Equal("1", table.GetProperty("rows")[1][0].GetString());
    }

    [Fact]
    public void DeeplyNestedMarkdownBecomesLiteralParagraphsThatRenderAgain()
    {
        var markdown = "# Title\n\n" + string.Concat(Enumerable.Repeat("> ", 200)) + "visible";
        var parser = new MarkdownJsonParser();
        Assert.False(parser.CanStructure(markdown));
        using var document = Parse(markdown, parser);
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Equal("paragraph", Type(document.RootElement[0]));
        Assert.Contains("Title", document.RootElement[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("visible", document.RootElement[1].GetProperty("text").GetString(), StringComparison.Ordinal);

        var restored = new MarkdownDocument();
        new MarkdownJsonFormatter().AppendTo(restored, document.RootElement);
        var pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseGridTables()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .DisableHtml()
            .Build();
        var html = Markdig.Markdown.ToHtml(restored.ToString(), pipeline);
        Assert.Contains("visible", html, StringComparison.Ordinal);
        Assert.Contains("Title", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t\n")]
    public void EmptyMarkdownProducesEmptyArray(string markdown)
    {
        using var document = Parse(markdown);
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        Assert.Throws<OperationCanceledException>(() => new MarkdownJsonParser().Write(writer, "# Title", new CancellationToken(true)));
    }

    [Fact]
    public async Task ParserCanBeSharedByConcurrentConversions()
    {
        var parser = new MarkdownJsonParser();
        await Task.WhenAll(Enumerable.Range(1, 20).Select(index => Task.Run(() =>
        {
            using var document = Parse($"# Title {index}", parser);
            Assert.Equal($"Title {index}", document.RootElement[0].GetProperty("text").GetString());
        })));
    }

    [Theory]
    [InlineData("# Title **bold**\n\nText *italic* [link](https://example.com) &amp; ![alt](image.png)")]
    [InlineData("3. First\n   - Nested\n\n> Quote **bold**\n\n- [x] Done\n- [ ] Pending")]
    [InlineData("| Name | Qty |\n| :--- | ---: |\n| **A** | 2 |")]
    [InlineData("```csharp\nvar value = 1;\n```\n\n$$\nx^2\n$$\n\n<div>HTML</div>\n\n---")]
    [InlineData("Setext **title**\n===\n\nLine one  \nLine two\n\n> Line one\n> Line two")]
    [InlineData("[link][ref] and ![alt][ref]\n\n[ref]: https://example.com \"Title\"")]
    [InlineData("| Name | Value |\n| --- | --- |\n| A | |")]
    [InlineData("Literal \\*not emphasis\\* and `**code**`\n\nText $E=mc^2$.")]
    [InlineData("10. First\n    - Nested\n      2. Deep\n\n11. Second")]
    [InlineData("- First paragraph\n\n  Second paragraph\n\n  > Quote\n\n  ```text\n  Code\n  ```")]
    [InlineData("> ```python\n> if ready:\n>     run()\n> ```")]
    [InlineData("- ```python\n  if ready:\n      run()\n  ```")]
    [InlineData("```python\nif ready:\n    run()\n```\n\n    indented code\n        nested indent")]
    [InlineData("- > ```python\n  > if ready:\n  >     run()\n  > ```")]
    [InlineData("> - ```python\n>   if ready:\n>       run()\n>   ```")]
    public void CompactJsonIsSufficientForTagBitesMarkdown(string markdown)
    {
        using var source = Parse(markdown);
        var restored = new MarkdownDocument();
        new MarkdownJsonFormatter().AppendTo(restored, source.RootElement);
        using var reparsed = Parse(restored.ToString());
        Assert.True(JsonElement.DeepEquals(source.RootElement, reparsed.RootElement),
            $"Generated Markdown: {restored}\nExpected: {source.RootElement}\nActual: {reparsed.RootElement}");
    }

    private static string? Type(JsonElement element) => element.GetProperty("type").GetString();

    private static JsonDocument Parse(string markdown, MarkdownJsonParser? parser = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            (parser ?? new MarkdownJsonParser()).Write(writer, markdown);
        }

        return JsonDocument.Parse(stream.ToArray());
    }
}
