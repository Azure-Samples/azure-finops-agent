using System.Collections.Concurrent;

namespace AzureFinOps.Dashboard.Infrastructure;

internal sealed class ToolExecutionContext : IDisposable
{
    private static readonly AsyncLocal<ToolExecutionContext?> Ambient = new();
    private readonly ToolExecutionContext? _previous;
    internal static ToolExecutionContext? Current => Ambient.Value;
    internal string? SessionId { get; }
    internal long? UserId { get; }
    internal CancellationToken CancellationToken { get; }

    /// <summary>
    /// Measured execution time of each function call by call ID. Parallel results reach the stream together once the
    /// whole batch finishes, so the stream alone would show every call as long as the slowest one.
    /// </summary>
    internal ConcurrentDictionary<string, long> ToolDurations { get; } = new(StringComparer.Ordinal);

    internal ToolExecutionContext(string? sessionId, long? userId, CancellationToken cancellationToken)
    {
        _previous = Ambient.Value;
        SessionId = sessionId;
        UserId = userId;
        CancellationToken = cancellationToken;
        Ambient.Value = this;
    }

    public void Dispose() => Ambient.Value = _previous;
}