using Microsoft.Extensions.Logging;

namespace MultiAgentCodeReview.McpServer.Logging;

public class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = File.AppendText(filePath);
        _writer.AutoFlush = true;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Write(string message)
    {
        lock (_lock) _writer.WriteLine(message);
    }

    public void Dispose() => _writer.Dispose();
}

public class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        provider.Write($"[{timestamp}] [{logLevel}] [{categoryName}] {message}");
    }
}

public static class FileLoggingExtensions
{
    public static ILoggingBuilder AddFileLogging(this ILoggingBuilder builder, string filePath)
    {
        builder.AddProvider(new FileLoggerProvider(filePath));
        return builder;
    }
}
