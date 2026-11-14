using Microsoft.Extensions.Logging;
using Mosaic.Contracts;

namespace Mosaic.Desktop;

internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object _sync = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    private void Write(string category, string message, Exception? exception)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"mosaic-{DateTime.UtcNow:yyyyMMdd}.log"), $"{DateTimeOffset.UtcNow:O} [{category}] {message}{Environment.NewLine}{exception}{Environment.NewLine}");
            }
            catch (IOException) { System.Diagnostics.Trace.WriteLine(message); }
            catch (UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(message); }
        }
    }
    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) owner.Write(category, formatter(state, exception), exception); }
    }
}

internal sealed class DiagnosticSink(ILogger<DiagnosticSink> logger) : IDiagnosticSink
{
    public void Report(string correlationId, Exception exception) => logger.LogError(exception, "Operation failed: {CorrelationId}", correlationId);
}
