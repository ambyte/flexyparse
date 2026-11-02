using FlexyParse.Options;
using Microsoft.Extensions.Options;

namespace FlexyParse.Pipeline;

public sealed class DocumentProcessingGate
{
    private readonly SemaphoreSlim _semaphore;

    public DocumentProcessingGate(IOptions<FlexyParseOptions> options)
    {
        var parallelism = options.Value.ResolveDocumentParallelism();
        _semaphore = new SemaphoreSlim(parallelism, parallelism);
    }

    public async Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
