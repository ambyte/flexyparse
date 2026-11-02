namespace FlexyParse.Storage;

public static class FileNameSanitizer
{
    public static string Sanitize(string? fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "document.pdf";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[name.Length];
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            buffer[index] = invalid.Contains(character) ? '_' : character;
        }

        var cleaned = new string(buffer).Trim();
        if (cleaned.Length == 0)
        {
            return "document.pdf";
        }

        return cleaned.Length <= 180 ? cleaned : cleaned[..180];
    }
}
