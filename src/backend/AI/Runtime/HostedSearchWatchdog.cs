namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// Ends a model run whose hosted web search stops making progress. A search normally finishes in a
/// few seconds, but when the service stalls on one nothing else ends the run until the network read
/// timeout, minutes later. Only a pending hosted search arms it and every streamed update re-arms it,
/// so reasoning, answers and function tools are never timed by it.
/// </summary>
internal sealed class HostedSearchWatchdog : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Shown when a hosted web search stalls or the service reports it incomplete.</summary>
    internal const string Message = "The web search did not finish, so this answer stopped. Ask again.";

    private readonly CancellationToken _outer;
    private readonly CancellationTokenSource _source;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;

    public HostedSearchWatchdog(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        _outer = cancellationToken;
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The run's token: the caller's cancellation plus a stalled search.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>The run was cancelled by a stalled search, not by the caller.</summary>
    public bool Stalled => _source.IsCancellationRequested && !_outer.IsCancellationRequested;

    public void Started(string callId) => _pending.Add(callId);

    public void Finished(string callId) => _pending.Remove(callId);

    /// <summary>Called after each streamed update: re-arms while a search is pending, disarms otherwise.</summary>
    public void Progress()
    {
        if (!_source.IsCancellationRequested)
            _source.CancelAfter(_pending.Count > 0 ? _timeout : Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>A run ended because its hosted web search stalled.</summary>
internal sealed class HostedSearchStalledException() : Exception(HostedSearchWatchdog.Message);
