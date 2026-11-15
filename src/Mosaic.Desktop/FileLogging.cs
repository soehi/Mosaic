using Microsoft.Extensions.Logging;
using Mosaic.Contracts;

namespace Mosaic.Desktop;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly int _retentionDays;

    internal FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, int retentionDays = 0)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        _retentionDays = retentionDays;
        CleanupOldLogs();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    private void Write(string category, string message, Exception? exception)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, $"mosaic-{DateTime.UtcNow:yyyyMMdd}.log"), $"{DateTimeOffset.UtcNow:O} [{category}] {message}{Environment.NewLine}{exception}{Environment.NewLine}");
            }
            catch (IOException) { System.Diagnostics.Trace.WriteLine(message); }
            catch (UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(message); }
        }
    }
    private void CleanupOldLogs()
    {
        if (_retentionDays <= 0 || !Directory.Exists(_directory)) return;
        var cutoff = DateTime.UtcNow.Date.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "mosaic-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith("mosaic-", StringComparison.OrdinalIgnoreCase)) continue;
            var dayText = name["mosaic-".Length..];
            if (DateTime.TryParseExact(dayText, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) &&
                day.Date < cutoff)
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= owner._minimumLevel;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) owner.Write(category, formatter(state, exception), exception); }
    }
}

internal sealed class DiagnosticSink(ILogger<DiagnosticSink> logger) : IDiagnosticSink
{
    public void Report(string correlationId, Exception exception) => logger.LogError(exception, "Operation failed: {CorrelationId}", correlationId);
}
