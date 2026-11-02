using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using FlexyParse.Options;
using FlexyParse.Pipeline;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FlexyParse.Hosting;

public sealed class AsyncConversionOptions
{
    public int WorkerCount { get; set; } = 2;
    public int QueueCapacity { get; set; } = 32;
    public int MaxStoredTasks { get; set; } = 128;
    public int ResultRetentionSeconds { get; set; } = 3600;
    public int CleanupIntervalSeconds { get; set; } = 60;
    public int MaxWebSocketConnections { get; set; } = 64;
}

public sealed record ConversionTaskStatus(
    [property: JsonPropertyName("task_id")] Guid TaskId,
    [property: JsonPropertyName("task_status")] string TaskStatus,
    [property: JsonPropertyName("task_position")] int? TaskPosition,
    [property: JsonPropertyName("task_meta")] object? TaskMeta = null);

/// <summary>Bounded, process-local task queue. Owns staged input and retained results after submission.</summary>
public sealed class ConversionTaskService : BackgroundService
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly Channel<Job> _queue;
    private readonly AsyncConversionOptions _options;
    private readonly FlexyParseOptions _conversionOptions;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConversionTaskService> _logger;
    private int _reservations;
    private int _pending;
    private int _webSockets;
    private long _sequence;
    private bool _stopping;

    public ConversionTaskService(IOptions<AsyncConversionOptions> options, IOptions<FlexyParseOptions> conversionOptions,
        IServiceScopeFactory scopes, TimeProvider clock, ILogger<ConversionTaskService> logger)
    {
        _options = options.Value;
        _conversionOptions = conversionOptions.Value;
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
        _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = _options.WorkerCount == 1,
            AllowSynchronousContinuations = false
        });
    }

    public static void AddServices(IServiceCollection services)
    {
        services.AddOptions<AsyncConversionOptions>().BindConfiguration("FlexyParse:Async")
            .Validate(options => options.WorkerCount > 0 && options.QueueCapacity > 0 && options.MaxStoredTasks > 0
                && options.ResultRetentionSeconds > 0 && options.CleanupIntervalSeconds > 0
                && options.MaxWebSocketConnections > 0, "All FlexyParse:Async limits must be positive.")
            .ValidateOnStart();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ConversionTaskService>();
        services.AddHostedService(provider => provider.GetRequiredService<ConversionTaskService>());
    }

    internal Admission Reserve()
    {
        CleanupExpired();
        lock (_gate)
        {
            if (_stopping || _jobs.Count + _reservations >= _options.MaxStoredTasks
                || _pending + _reservations >= _options.QueueCapacity)
            {
                throw new DocumentConversionException(StatusCodes.Status503ServiceUnavailable,
                    "The asynchronous task queue or retained task limit is full. Retry later.");
            }
            _reservations++;
            return new Admission(this);
        }
    }

    internal sealed class Admission(ConversionTaskService owner) : IDisposable
    {
        private ConversionTaskService? _owner = owner;

        public ConversionTaskStatus Submit(ConversionBatch batch, Stopwatch stopwatch)
        {
            var service = Interlocked.Exchange(ref _owner, null) ?? throw new InvalidOperationException("Admission already released.");
            lock (service._gate)
            {
                service._reservations--;
                if (service._stopping)
                {
                    throw new DocumentConversionException(StatusCodes.Status503ServiceUnavailable, "The server is stopping.");
                }
                var job = new Job(batch, stopwatch, ++service._sequence);
                service._jobs.Add(job.Id, job);
                service._pending++;
                if (!service._queue.Writer.TryWrite(job))
                {
                    service._jobs.Remove(job.Id);
                    service._pending--;
                    throw new DocumentConversionException(StatusCodes.Status503ServiceUnavailable, "The asynchronous task queue is full.");
                }
                return service.Snapshot(job);
            }
        }

        public void Dispose()
        {
            var service = Interlocked.Exchange(ref _owner, null);
            if (service is not null)
            {
                lock (service._gate) { service._reservations--; }
            }
        }
    }

    internal (ConversionTaskStatus Status, Task Changed)? GetUpdate(Guid id)
    {
        CleanupExpired();
        lock (_gate)
        {
            return _jobs.TryGetValue(id, out var job) && !Expired(job)
                ? (Snapshot(job), job.Changed.Task) : null;
        }
    }

    internal IResult GetResult(Guid id)
    {
        CleanupExpired();
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || Expired(job))
            {
                return Results.Problem("Task not found or expired.", statusCode: StatusCodes.Status404NotFound);
            }
            if (job.State is "pending" or "started")
            {
                return Results.Problem("The task has not finished.", statusCode: StatusCodes.Status409Conflict);
            }
            if (job.State == "failure")
            {
                return Results.Problem(job.Error, statusCode: job.ErrorStatus);
            }

            var result = job.Result!;
            // Acquire the file and lease under the registry lock so expiration cannot delete it.
            var stream = new LeasedResultStream(result.Path, () => ReleaseReader(job));
            job.Readers++;
            return Results.Stream(stream, result.ContentType, result.DownloadName);
        }
    }

    internal bool TryOpenWebSocket()
    {
        lock (_gate)
        {
            if (_stopping || _webSockets >= _options.MaxWebSocketConnections) { return false; }
            _webSockets++;
            return true;
        }
    }

    internal void CloseWebSocket() { lock (_gate) { _webSockets--; } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var workers = Enumerable.Range(0, _options.WorkerCount).Select(_ => WorkAsync(stoppingToken));
            await Task.WhenAll(workers.Append(CleanupAsync(stoppingToken))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Job[] remaining;
            lock (_gate)
            {
                _stopping = true;
                _queue.Writer.TryComplete();
                remaining = _jobs.Values.ToArray();
                foreach (var job in remaining)
                {
                    if (job.State is "pending" or "started")
                    {
                        job.State = "failure";
                        job.Error = "The server is stopping.";
                        job.ErrorStatus = StatusCodes.Status503ServiceUnavailable;
                        job.CompletedAt = _clock.GetUtcNow();
                        Signal(job);
                    }
                }
                _pending = 0;
            }
            foreach (var job in remaining.Where(job => job.Readers == 0)) { job.Batch.Dispose(); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { _stopping = true; _queue.Writer.TryComplete(); }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WorkAsync(CancellationToken token)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (token.IsCancellationRequested) { break; }
            lock (_gate)
            {
                job.State = "started";
                _pending--;
                Signal(job);
                foreach (var pending in _jobs.Values.Where(item => item.State == "pending")) { Signal(pending); }
            }
            try
            {
                using var scope = _scopes.CreateScope();
                if (job.Batch.SourceRequestPath is not null)
                {
                    await scope.ServiceProvider.GetRequiredService<SourceDocumentService>()
                        .ResolveAsync(job.Batch, _conversionOptions, token).ConfigureAwait(false);
                }
                var result = await ConversionResultBuilder.BuildAsync(job.Batch,
                    scope.ServiceProvider.GetRequiredService<IDocumentConverter>(), _conversionOptions, job.Stopwatch, token).ConfigureAwait(false);
                // Keep only the final result for the retention period, not uploaded source documents.
                if (!_conversionOptions.KeepResults)
                {
                    foreach (var path in Directory.EnumerateFiles(job.Batch.Directory).Where(path => path != result.Path)) { File.Delete(path); }
                }
                lock (_gate) { job.Result = result; job.State = "success"; }
            }
            catch (Exception exception)
            {
                job.Batch.Dispose();
                lock (_gate)
                {
                    job.State = "failure";
                    job.ErrorStatus = exception is DocumentConversionException conversion ? conversion.StatusCode
                        : token.IsCancellationRequested ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
                    job.Error = exception is DocumentConversionException expected ? expected.Message
                        : token.IsCancellationRequested ? "The server is stopping." : "Background conversion failed.";
                }
                if (exception is not DocumentConversionException && !token.IsCancellationRequested)
                {
                    // Do not log exception messages: HTTP source exceptions can contain signed URLs.
                    _logger.LogError("Background task {TaskId} failed with {ExceptionType}.", job.Id, exception.GetType().Name);
                }
            }
            finally
            {
                lock (_gate) { job.CompletedAt = _clock.GetUtcNow(); Signal(job); }
            }
        }
    }

    private async Task CleanupAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.CleanupIntervalSeconds));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) { CleanupExpired(); }
    }

    private void CleanupExpired()
    {
        List<Job> expired;
        lock (_gate)
        {
            expired = _jobs.Values.Where(job => job.Readers == 0 && Expired(job)).ToList();
            foreach (var job in expired) { _jobs.Remove(job.Id); Signal(job); }
        }
        foreach (var job in expired) { job.Batch.Dispose(); }
    }

    private bool Expired(Job job) => job.CompletedAt is { } completed
        && _clock.GetUtcNow() - completed >= TimeSpan.FromSeconds(_options.ResultRetentionSeconds);

    private ConversionTaskStatus Snapshot(Job job) => new(job.Id, job.State,
        job.State == "pending" ? _jobs.Values.Count(item => item.State == "pending" && item.Sequence <= job.Sequence) : null);

    private static void Signal(Job job)
    {
        var changed = job.Changed;
        job.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    private void ReleaseReader(Job job)
    {
        bool delete;
        lock (_gate) { job.Readers--; delete = _stopping && job.Readers == 0; }
        if (delete) { job.Batch.Dispose(); }
        else { CleanupExpired(); }
    }

    private sealed class Job(ConversionBatch batch, Stopwatch stopwatch, long sequence)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ConversionBatch Batch { get; } = batch;
        public Stopwatch Stopwatch { get; } = stopwatch;
        public long Sequence { get; } = sequence;
        public string State { get; set; } = "pending";
        public ConversionResult? Result { get; set; }
        public string? Error { get; set; }
        public int ErrorStatus { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int Readers { get; set; }
        public TaskCompletionSource Changed { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class LeasedResultStream(string path, Action release)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous)
    {
        private Action? _release = release;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { Interlocked.Exchange(ref _release, null)?.Invoke(); }
        }
    }
}
