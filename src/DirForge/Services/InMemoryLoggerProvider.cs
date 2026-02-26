using DirForge.Models;
using Microsoft.Extensions.Logging;

namespace DirForge.Services;

public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly InMemoryLogStore _store;

    public InMemoryLoggerProvider(InMemoryLogStore store)
    {
        _store = store;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new InMemoryLogger(categoryName, _store);
    }

    public void Dispose()
    {
    }

    private sealed class InMemoryLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly InMemoryLogStore _store;

        public InMemoryLogger(string categoryName, InMemoryLogStore store)
        {
            _categoryName = categoryName;
            _store = store;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _store.Add(new InMemoryLogEntry(
                TimestampUtc: DateTimeOffset.UtcNow,
                Level: logLevel,
                Category: _categoryName,
                Message: formatter(state, exception),
                Exception: exception?.ToString()));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
