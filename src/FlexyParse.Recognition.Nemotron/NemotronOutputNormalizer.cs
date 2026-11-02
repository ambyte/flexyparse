using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace FlexyParse.Nemotron;

/// <summary>Preserves Markdown and adapts layout-marked LaTeX tables to the shared Markdown contract.</summary>
internal static partial class NemotronOutputNormalizer
{
    // Bound model-generated spans rather than allocating arbitrary column counts.
    private const int MaxColumns = 256;
    private static readonly MarkdownPipeline CodePipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .Build();

    public static string Normalize(string markdown, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OutputParts().IsMatch(markdown)) return markdown;
        // Use Markdown source spans rather than guessing fence lengths or interpreting inline code.
        // This also protects indented blocks and fences left open by truncated model output.
        var codeSpans = global::Markdig.Markdown.Parse(markdown, CodePipeline).Descendants()
            .Where(node => node is CodeBlock or CodeInline)
            .Select(node =>
            {
                var span = node.Span;
                if (node is CodeBlock code && code.Lines.Count > 0)
                {
                    // An unclosed fence's span can end at its opening line. Include its actual
                    // content lines, not the rest of the document (a containing quote/list may end).
                    var lastLine = code.Lines.Lines[code.Lines.Count - 1];
                    span.End = Math.Max(span.End, lastLine.Position + lastLine.Slice.Length - 1);
                }
                return span;
            })
            .OrderBy(span => span.Start)
            .ToArray();
        var codeIndex = 0;
        return OutputParts().Replace(markdown, match =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (codeIndex < codeSpans.Length && codeSpans[codeIndex].End < match.Index) codeIndex++;
            if (codeIndex < codeSpans.Length && codeSpans[codeIndex].Start < match.Index + match.Length)
                return match.Value;
            if (match.Groups["control"].Success) return "";
            return ConvertTable(match.Value, cancellationToken);
        });
    }

    private static string ConvertTable(string source, CancellationToken token)
    {
        const string begin = @"\begin{tabular}";
        const string end = @"\end{tabular}";
        var position = begin.Length;
        SkipWhitespace(source, ref position);
        // Optional vertical alignment, e.g. \begin{tabular}[t]{cc}.
        if (position < source.Length && source[position] == '[')
        {
            var close = source.IndexOf(']', position);
            if (close < 0) return source;
            position = close + 1;
        }
        if (!TryReadGroup(source, ref position, out _)) return source;
        var body = source[position..^end.Length];
        body = Rules().Replace(body, "");
        if (!TrySplit(body, rows: true, out var rawRows)) return source;
        var rows = new List<List<string>>();
        foreach (var rawRow in rawRows)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawRow)) continue;
            if (!TrySplit(rawRow, rows: false, out var rawCells)) return source;
            var cells = new List<string>();
            foreach (var rawCell in rawCells)
            {
                if (!TryConvertCell(rawCell, out var text, out var span)) return source;
                if (cells.Count + span > MaxColumns) return source;
                cells.Add(text);
                for (var i = 1; i < span; i++) cells.Add("");
            }
            rows.Add(cells);
        }
        if (rows.Count == 0) return source;
        // OCR can produce rows wider than the declared alignment: never truncate their cells.
        var width = rows.Max(row => row.Count);
        var output = new StringBuilder("\n\n");
        for (var i = 0; i < rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            output.Append('|');
            for (var column = 0; column < width; column++)
                output.Append(' ').Append(column < rows[i].Count ? rows[i][column] : "").Append(" |");
            output.AppendLine();
            if (i == 0)
            {
                output.Append('|');
                for (var column = 0; column < width; column++) output.Append(" --- |");
                output.AppendLine();
            }
        }
        return output.AppendLine().ToString();
    }

    private static bool TryConvertCell(string source, out string text, out int span)
    {
        text = source.Trim();
        span = 1;
        // Unwrap only recognized whole-cell commands; unknown LaTeX remains literal text.
        for (var depth = 0; depth < 16; depth++)
        {
            var command = CellCommand().Match(text);
            if (!command.Success) break;
            var position = command.Length;
            if (!TryReadGroup(text, ref position, out var first)) return false;
            var name = command.Groups[1].Value;
            if (name is "multicolumn" or "multirow")
            {
                if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                    || count is < 1 or > MaxColumns) return false;
                if (name == "multicolumn") span = count;
                if (!TryReadGroup(text, ref position, out _)
                    || !TryReadGroup(text, ref position, out first)) return false;
            }
            if (!string.IsNullOrWhiteSpace(text[position..])) break;
            text = name is "textbf" or "mathbf" ? "**" + first + "**" : first;
        }
        text = text.Replace(@"\&", "&", StringComparison.Ordinal)
            .Replace(@"\%", "%", StringComparison.Ordinal)
            .Replace(@"\_", "_", StringComparison.Ordinal)
            .Replace(@"\#", "#", StringComparison.Ordinal)
            .Replace(@"\$", "$", StringComparison.Ordinal);
        // Pipe tables must remain rectangular even when OCR text contains a literal pipe/newline.
        text = text.Replace("|", "\\|", StringComparison.Ordinal);
        text = Whitespace().Replace(text, " ").Trim();
        return true;
    }

    private static bool TrySplit(string text, bool rows, out List<string> parts)
    {
        parts = [];
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                if (text[i + 1] == '\\' && rows && depth == 0)
                {
                    parts.Add(text[start..i]);
                    i++;
                    start = i + 1;
                    // A LaTeX row may specify extra vertical spacing: \\[2pt].
                    var next = start;
                    SkipWhitespace(text, ref next);
                    if (next < text.Length && text[next] == '[')
                    {
                        var close = text.IndexOf(']', next);
                        if (close < 0) return false;
                        i = close;
                        start = close + 1;
                    }
                }
                else if (text[i + 1] is '&' or '{' or '}' or '\\') i++;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}' && --depth < 0) return false;
            else if (c == '&' && !rows && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return depth == 0;
    }

    private static bool TryReadGroup(string text, ref int position, out string value)
    {
        value = "";
        SkipWhitespace(text, ref position);
        if (position >= text.Length || text[position] != '{') return false;
        var start = ++position;
        var depth = 1;
        for (; position < text.Length; position++)
        {
            if (text[position] == '\\' && position + 1 < text.Length
                && text[position + 1] is '{' or '}' or '\\') { position++; continue; }
            if (text[position] == '{') depth++;
            else if (text[position] == '}' && --depth == 0)
            {
                value = text[start..position++];
                return true;
            }
        }
        return false;
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
    }

    // Strip only known layout/control markers, never arbitrary HTML; code is protected by source spans.
    [GeneratedRegex(@"(?<control><(?:[xy]_[0-9]+(?:\.[0-9]+)?|class_[A-Za-z_]+|no_bbox|no_classes|output_markdown|predict_text_in_pic)>)|\\begin\{tabular\}[\s\S]*?\\end\{tabular\}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex OutputParts();

    [GeneratedRegex(@"\\(?:hline|toprule|midrule|bottomrule)\b|\\(?:cline|cmidrule)\s*\{[0-9]+-[0-9]+\}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Rules();

    [GeneratedRegex(@"^\\(multicolumn|multirow|textbf|mathbf|textit|textrm|text)\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CellCommand();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Whitespace();
}
