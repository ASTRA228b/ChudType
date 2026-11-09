using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ChudType.Storage;

internal sealed class LocalLogProvider(LocalStore store) : ILoggerProvider
{
    private readonly object gate = new();
    private readonly string path = Path.Combine(store.DirectoryPath, "events.jsonl");

    public ILogger CreateLogger(string categoryName) => new LocalLogger(this, categoryName);
    public void Dispose() { }

    private sealed class LocalLogger(LocalLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var entry = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.UtcNow, level = level.ToString(), category,
                eventId = eventId.Id, message = formatter(state, exception),
                exception = exception?.GetType().Name
            });
            var path = owner.path;
            lock (owner.gate)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                        File.Move(path, path + ".old", overwrite: true);
                    File.AppendAllText(path, entry + Environment.NewLine);
                }
                catch (IOException) { System.Diagnostics.Trace.TraceWarning("ChudType could not write its event log."); }
                catch (UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("ChudType cannot access its event log."); }
            }
        }
    }
}
