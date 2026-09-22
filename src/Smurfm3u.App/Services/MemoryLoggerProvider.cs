using Smurfm3u.Core.Diagnostics;

namespace Smurfm3u.App.Services;

/// <summary>
/// Copies every line the logging system emits into <see cref="LogRing"/> as well as wherever
/// else it was going. Registered as one provider among several, so the console output is
/// untouched and the level filters in appsettings decide what reaches here too.
/// </summary>
[ProviderAlias("Memory")]
public sealed class MemoryLoggerProvider(LogRing ring, TimeProvider clock) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new MemoryLogger(categoryName, ring, clock);

    public void Dispose()
    {
    }

    private sealed class MemoryLogger(string category, LogRing ring, TimeProvider clock) : ILogger
    {
        /// <summary>
        /// Scopes are not kept. Nothing here opens one, and holding them would mean tracking
        /// state per thread for a page that shows one line at a time.
        /// </summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The composite logger has already applied the configured filters by the time it gets
        // here, so anything that arrives is something the configuration wanted kept.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            ring.Add(logLevel, category, formatter(state, exception), exception?.ToString(), clock.GetUtcNow());
        }
    }
}
