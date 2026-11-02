using FlexyParse.Pipeline;

namespace FlexyParse.Storage;

public static class LimitedStreamCopy
{
    public static Task CopyAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken) =>
        CopyAsync(source, destination, maxBytes, cancellationToken, null);

    public static async Task CopyAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken,
        Action<long>? reportProgress)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new DocumentConversionException(ConversionStatus.PayloadTooLarge, "The file exceeds the allowed size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            reportProgress?.Invoke(total);
        }
    }
}
