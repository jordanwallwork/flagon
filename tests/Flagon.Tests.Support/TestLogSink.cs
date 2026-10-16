using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Flagon.Tests.Support;

/// <summary>A log entry captured by <see cref="TestLogSink"/>.</summary>
public sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>An <see cref="ILoggerProvider"/> that records every log entry so tests can assert on logging behaviour.</summary>
public sealed class TestLogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

    /// <summary>Entries at <paramref name="level"/> whose category starts with "Flagon".</summary>
    public IReadOnlyList<CapturedLog> Flagon(LogLevel level) =>
        Entries.Where(e => e.Level == level && e.Category.StartsWith("Flagon", StringComparison.Ordinal)).ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public void Dispose() { }

    private sealed class Logger(string category, TestLogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink._entries.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception), exception));
    }
}
