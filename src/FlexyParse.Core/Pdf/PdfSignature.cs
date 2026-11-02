using System.Text;

namespace FlexyParse.Pdf;

public static class PdfSignature
{
    public static bool HasPdfHeader(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> buffer = stackalloc byte[1024];
        var read = stream.Read(buffer);
        if (read <= 0)
        {
            return false;
        }

        var header = Encoding.ASCII.GetString(buffer[..read]);
        return header.Contains("%PDF-", StringComparison.Ordinal);
    }
}
