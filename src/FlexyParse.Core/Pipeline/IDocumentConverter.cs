namespace FlexyParse.Pipeline;

public interface IDocumentConverter
{
    /// <summary>Converts a document and releases its temporary workspace on completion, failure, or cancellation.</summary>
    /// <remarks>
    /// Cancellation is cooperative. The caller owns the input and destination streams and must discard
    /// any partially written output after cancellation. Synchronous third-party operations finish before
    /// cancellation can be observed. Moving a source file is not rolled back.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was cancelled.</exception>
    Task ConvertAsync(ConversionInput input, Stream destination, CancellationToken cancellationToken);
}
