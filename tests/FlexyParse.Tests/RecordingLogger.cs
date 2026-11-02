using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FlexyParse.Tests;

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<RecordedLog> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>();
        Entries.Enqueue(new RecordedLog(logLevel, formatter(state, exception), properties));
    }
}

internal sealed record RecordedLog(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties);
