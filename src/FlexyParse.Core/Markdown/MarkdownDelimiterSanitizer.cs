using System.Text;

namespace FlexyParse.Markdown;

/// <summary>
/// Markdig turns a long run of emphasis characters into nested inline nodes and then refuses to render
/// past its nesting limit. Form underlines transcribed as underscores are the usual source.
/// </summary>
internal static class MarkdownDelimiterSanitizer
{
    internal const char Line = '\u2500';

    private const int CollapseLength = 4;

    // Markdig stops rendering at 128 nested nodes. Probe earlier so a direct write never starts that tree.
    private const int ProbeThreshold = 96;

    internal static string Collapse(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !ContainsCollapsibleRun(markdown))
        {
            return markdown;
        }

        var builder = new StringBuilder(markdown.Length);
        var fence = '\0';
        var fenceLength = 0;
        var lineStart = 0;
        for (var index = 0; index <= markdown.Length; index++)
        {
            if (index < markdown.Length && markdown[index] != '\n')
            {
                continue;
            }

            var contentEnd = index;
            var carriageReturn = contentEnd > lineStart && markdown[contentEnd - 1] == '\r';
            if (carriageReturn)
            {
                contentEnd--;
            }

            var line = markdown.AsSpan(lineStart, contentEnd - lineStart);
            if (IsFenceLine(ref fence, ref fenceLength, line) || IsPreservedLine(line))
            {
                builder.Append(line);
            }
            else
            {
                AppendCollapsed(builder, line);
            }

            if (index < markdown.Length)
            {
                if (carriageReturn)
                {
                    builder.Append("\r\n");
                }
                else
                {
                    builder.Append('\n');
                }
            }

            lineStart = index + 1;
        }

        return builder.Equals(markdown.AsSpan()) ? markdown : builder.ToString();
    }

    internal static bool MayExceedNestingDepth(string markdown)
    {
        var fence = '\0';
        var fenceLength = 0;
        var delimiters = 0;
        var lineStart = 0;
        for (var index = 0; index <= markdown.Length; index++)
        {
            if (index < markdown.Length && markdown[index] != '\n')
            {
                continue;
            }

            var contentEnd = index;
            if (contentEnd > lineStart && markdown[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            var line = markdown.AsSpan(lineStart, contentEnd - lineStart);
            lineStart = index + 1;
            if (IsFenceLine(ref fence, ref fenceLength, line))
            {
                delimiters = 0;
                continue;
            }

            if (line.IsWhiteSpace() || IsPreservedLine(line))
            {
                delimiters = 0;
                continue;
            }

            if (QuoteDepth(line) >= ProbeThreshold || LeadingIndent(line) >= ProbeThreshold)
            {
                return true;
            }

            foreach (var character in line)
            {
                if (IsEmphasisDelimiter(character))
                {
                    delimiters++;
                }
            }

            if (delimiters >= ProbeThreshold)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsDepthLimit(Exception exception) =>
        exception is ArgumentException
        && exception.Message.Contains("too deeply nested", StringComparison.Ordinal);

    internal static string EscapeLiteral(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (character == '\r')
            {
                continue;
            }

            if (character == '\n')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (character is '\\' or '`' or '*' or '_' or '~' or '^' or '+' or '=' or '[' or ']' or '|' or '<' or '>' or '#')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static bool ContainsCollapsibleRun(string markdown)
    {
        var run = '\0';
        var count = 0;
        foreach (var character in markdown)
        {
            if (character == run && IsEmphasisDelimiter(character))
            {
                count++;
                if (count >= CollapseLength)
                {
                    return true;
                }

                continue;
            }

            run = IsEmphasisDelimiter(character) ? character : '\0';
            count = run == '\0' ? 0 : 1;
        }

        return false;
    }

    private static void AppendCollapsed(StringBuilder builder, ReadOnlySpan<char> line)
    {
        var index = 0;
        while (index < line.Length)
        {
            if (line[index] == '`')
            {
                var ticks = 1;
                while (index + ticks < line.Length && line[index + ticks] == '`')
                {
                    ticks++;
                }

                var close = IndexOfTickRun(line, index + ticks, ticks);
                if (close < 0)
                {
                    AppendDelimiters(builder, line[index..]);
                    return;
                }

                builder.Append(line[index..(close + ticks)]);
                index = close + ticks;
                continue;
            }

            if (!IsEmphasisDelimiter(line[index]))
            {
                builder.Append(line[index]);
                index++;
                continue;
            }

            var end = index + 1;
            while (end < line.Length && line[end] == line[index])
            {
                end++;
            }

            var count = end - index;
            if (count >= CollapseLength)
            {
                builder.Append(Line, count);
            }
            else
            {
                builder.Append(line[index..end]);
            }

            index = end;
        }
    }

    private static void AppendDelimiters(StringBuilder builder, ReadOnlySpan<char> text)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (!IsEmphasisDelimiter(text[index]))
            {
                builder.Append(text[index]);
                index++;
                continue;
            }

            var end = index + 1;
            while (end < text.Length && text[end] == text[index])
            {
                end++;
            }

            var count = end - index;
            if (count >= CollapseLength)
            {
                builder.Append(Line, count);
            }
            else
            {
                builder.Append(text[index..end]);
            }

            index = end;
        }
    }

    private static int IndexOfTickRun(ReadOnlySpan<char> line, int start, int ticks)
    {
        for (var index = start; index <= line.Length - ticks; index++)
        {
            if (line[index] != '`')
            {
                continue;
            }

            var length = 1;
            while (index + length < line.Length && line[index + length] == '`')
            {
                length++;
            }

            if (length == ticks)
            {
                return index;
            }

            index += length - 1;
        }

        return -1;
    }

    private static bool IsFenceLine(ref char marker, ref int length, ReadOnlySpan<char> line)
    {
        if (marker != '\0')
        {
            if (IsClosingFence(line, marker, length))
            {
                marker = '\0';
                length = 0;
            }

            return true;
        }

        if (!TryOpenFence(line, out var opened, out var openedLength))
        {
            return false;
        }

        marker = opened;
        length = openedLength;
        return true;
    }

    private static bool TryOpenFence(ReadOnlySpan<char> line, out char marker, out int length)
    {
        marker = '\0';
        length = 0;
        if (!TryReadMarkerRun(line, out _, out marker, out length, out var runEnd) || length < 3 || marker is not ('`' or '~'))
        {
            return false;
        }

        return marker != '`' || !line[runEnd..].Contains('`');
    }

    private static bool IsClosingFence(ReadOnlySpan<char> line, char marker, int length)
    {
        if (!TryReadMarkerRun(line, out _, out var runMarker, out var runLength, out var runEnd)
            || runMarker != marker
            || runLength < length)
        {
            return false;
        }

        for (var index = runEnd; index < line.Length; index++)
        {
            if (line[index] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadMarkerRun(ReadOnlySpan<char> line, out int contentStart, out char marker, out int length, out int runEnd)
    {
        contentStart = 0;
        marker = '\0';
        length = 0;
        runEnd = 0;
        var indent = 0;
        while (contentStart < line.Length && line[contentStart] == ' ' && indent < 3)
        {
            indent++;
            contentStart++;
        }

        if (contentStart >= line.Length)
        {
            return false;
        }

        marker = line[contentStart];
        var index = contentStart;
        while (index < line.Length && line[index] == marker)
        {
            index++;
        }

        length = index - contentStart;
        runEnd = index;
        return length > 0;
    }

    private static bool IsPreservedLine(ReadOnlySpan<char> line) =>
        IsThematicBreak(line) || IsSetextUnderline(line) || IsTableDelimiterLine(line);

    private static bool IsThematicBreak(ReadOnlySpan<char> line)
    {
        var index = 0;
        var indent = 0;
        while (index < line.Length && line[index] == ' ')
        {
            indent++;
            index++;
            if (indent > 3)
            {
                return false;
            }
        }

        if (index >= line.Length || line[index] is not ('-' or '_' or '*'))
        {
            return false;
        }

        var marker = line[index];
        var count = 0;
        for (; index < line.Length; index++)
        {
            if (line[index] == marker)
            {
                count++;
            }
            else if (line[index] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return count >= 3;
    }

    private static bool IsSetextUnderline(ReadOnlySpan<char> line)
    {
        var index = 0;
        var indent = 0;
        while (index < line.Length && line[index] == ' ')
        {
            indent++;
            index++;
            if (indent > 3)
            {
                return false;
            }
        }

        if (index >= line.Length || line[index] is not ('=' or '-'))
        {
            return false;
        }

        var marker = line[index];
        while (index < line.Length && line[index] == marker)
        {
            index++;
        }

        while (index < line.Length && line[index] is ' ' or '\t')
        {
            index++;
        }

        return index == line.Length;
    }

    private static bool IsTableDelimiterLine(ReadOnlySpan<char> line)
    {
        var junction = false;
        var content = false;
        foreach (var character in line)
        {
            if (character is ' ' or '\t')
            {
                continue;
            }

            if (character is not ('+' or '-' or '=' or ':' or '|'))
            {
                return false;
            }

            content = true;
            if (character is '+' or '|')
            {
                junction = true;
            }
        }

        return content && junction;
    }

    private static int QuoteDepth(ReadOnlySpan<char> line)
    {
        var depth = 0;
        var index = 0;
        while (index < line.Length)
        {
            while (index < line.Length && line[index] is ' ' or '\t')
            {
                index++;
            }

            if (index >= line.Length || line[index] != '>')
            {
                break;
            }

            depth++;
            index++;
        }

        return depth;
    }

    private static int LeadingIndent(ReadOnlySpan<char> line)
    {
        var indent = 0;
        foreach (var character in line)
        {
            if (character == ' ')
            {
                indent++;
            }
            else if (character == '\t')
            {
                indent += 4;
            }
            else
            {
                break;
            }
        }

        return indent;
    }

    private static bool IsEmphasisDelimiter(char character) =>
        character is '*' or '_' or '~' or '^' or '+' or '=';
}
