using SkiaSharp;

namespace FlexyParse.RapidOcr;

/// <summary>A single owned, non-thread-safe local OCR session. No network access is permitted.</summary>
public interface IRapidOcrSession : IDisposable
{
    Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken);
}

/// <summary>Creates sessions from resolved local model files. Provisioning is handled by RapidOcrModelStore.</summary>
public interface IRapidOcrSessionFactory
{
    IRapidOcrSession Create(RapidOcrOptions options, RapidOcrModelProfile profile);
}

public sealed class RapidOcrSessionFactory : IRapidOcrSessionFactory
{
    public IRapidOcrSession Create(RapidOcrOptions options, RapidOcrModelProfile profile) => new Session(options, profile);

    private sealed class Session : IRapidOcrSession
    {
        private readonly RapidOcrNet.RapidOcr _engine = new();
        private readonly RapidOcrOptions _options;

        public Session(RapidOcrOptions options, RapidOcrModelProfile profile)
        {
            _options = options;
            try
            {
                _engine.InitModels(options.DetectionModelPath, options.ClassificationModelPath,
                    profile.RecognitionModelPath, profile.DictionaryPath, options.InferenceThreads);
            }
            catch
            {
                _engine.Dispose();
                throw;
            }
        }

        public Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = File.OpenRead(imagePath);
            using var codec = SKCodec.Create(input) ?? throw new InvalidDataException("Unsupported local OCR image format.");
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || info.Width > _options.MaxImageDimension
                || info.Height > _options.MaxImageDimension || (long)info.Width * info.Height > _options.MaxImagePixels)
            {
                throw new InvalidDataException("Local OCR image exceeds configured dimension or pixel limits.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Unable to decode local OCR image.");
            var result = _engine.Detect(bitmap, RapidOcrNet.RapidOcrOptions.Default, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result.StrRes;
        }, cancellationToken);

        public void Dispose() => _engine.Dispose();
    }
}
