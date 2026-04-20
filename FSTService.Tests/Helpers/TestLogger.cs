using Microsoft.Extensions.Logging;

namespace FSTService.Tests.Helpers;

public sealed class TestLogger<T> : ILogger<T>
{
    private readonly List<TestLogEntry> _entries = [];

    public IReadOnlyList<TestLogEntry> Entries => _entries;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Add(new TestLogEntry(logLevel, eventId, formatter(state, exception), exception));
    }

    public sealed record TestLogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}