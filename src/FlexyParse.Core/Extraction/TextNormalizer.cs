using System.Text;

namespace FlexyParse.Extraction;

public static class TextNormalizer
{
    public static string NormalizeBlock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var builder = new StringBuilder(normalized.Length);
        var paragraphBreak = false;
        foreach (var line in normalized.Split('\n'))
        {
            var collapsed = Collapse(line);
            if (collapsed.Length == 0)
            {
                paragraphBreak = builder.Length > 0;
                continue;
            }

            if (builder.Length > 0)
            {
                // PDF line wrapping is visual, not a paragraph boundary. Keep explicit
                // blank lines and list items, but join continuation lines with a space.
                builder.Append(paragraphBreak ? "\n\n" : StartsListItem(collapsed) ? "\n" : " ");
            }

            builder.Append(collapsed);
            paragraphBreak = false;
        }

        return builder.ToString().Normalize(NormalizationForm.FormKC);
    }

    private static bool StartsListItem(string line)
    {
        if (line.Length > 1 && line[1] == ' ' && line[0] is '-' or '*' or '+' or '•' or '◦' or '▪' or '–' or '—')
        {
            return true;
        }

        var index = 0;
        while (index < line.Length && char.IsAsciiDigit(line[index]))
        {
            index++;
        }

        return index > 0 && index + 1 < line.Length && line[index] is '.' or ')' && line[index + 1] == ' ';
    }

    public static string NormalizeCell(string? text)
    {
        return Collapse(text).Normalize(NormalizationForm.FormKC);
    }

    public static string Collapse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
