namespace FlexyParse.Pipeline;

public static class ConversionStatus
{
    public const int BadRequest = 400;

    public const int PayloadTooLarge = 413;

    public const int ServiceUnavailable = 503;
}

public sealed class DocumentConversionException : Exception
{
    public DocumentConversionException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public DocumentConversionException(int statusCode, string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
