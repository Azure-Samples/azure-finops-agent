using Microsoft.Extensions.Logging;

namespace LiveEvaluations;

/// <summary>
/// Keeps the evaluated host's warnings and errors for the case's private diagnostics. The suite discards the case
/// process's output, so without this a resent model request or a failed turn left no trace.
/// </summary>
public sealed class HostWarningCapture(EvaluationRunState state) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(state, categoryName);

    public void Dispose() { }

    private sealed class Logger(EvaluationRunState state, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState scopeState) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState logState, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var failure = exception is null ? "" : $" [{exception.GetType().Name}: {exception.Message.Split('\n')[0]}]";
            state.RecordHostWarning($"{state.Clock.ElapsedMilliseconds} ms {logLevel} {category}: {formatter(logState, exception)}{failure}");
        }
    }
}
