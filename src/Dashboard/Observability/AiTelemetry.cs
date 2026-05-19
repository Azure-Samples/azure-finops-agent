using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.Observability;

/// <summary>
/// Shared OpenTelemetry primitives + the app's per-user runtime state.
/// Chat sessions are stateless — there is no server-side tracking of
/// conversation history, titles, or "current session". Per-user data here is
/// purely runtime (tokens, tool closures) and evicted by <see cref="UserStateJanitor"/>.
/// </summary>
public sealed class AiTelemetry
{
    public ActivitySource ActivitySource { get; } = new("AzureFinOps.AI");
    public Meter Meter { get; }

    public Counter<long> ChatRequests { get; }
    public Counter<long> ChatErrors { get; }
    public Counter<long> ToolCalls { get; }
    public Counter<long> ToolErrors { get; }
    public UpDownCounter<long> ActiveSessions { get; }
    public Histogram<double> ChatDuration { get; }

    public ConcurrentDictionary<long, UserTokens> UserTokens { get; } = new();
    public ConcurrentDictionary<long, List<AIFunction>> UserTools { get; } = new();

    public AiTelemetry()
    {
        Meter = new Meter("AzureFinOps.AI");
        ChatRequests = Meter.CreateCounter<long>("finops.chat.requests", description: "Total chat requests");
        ChatErrors = Meter.CreateCounter<long>("finops.chat.errors", description: "Chat request errors");
        ToolCalls = Meter.CreateCounter<long>("finops.tool.calls", description: "Tool call invocations");
        ToolErrors = Meter.CreateCounter<long>("finops.tool.errors", description: "Tool call errors");
        ActiveSessions = Meter.CreateUpDownCounter<long>("finops.sessions.active", description: "Currently active chat sessions");
        ChatDuration = Meter.CreateHistogram<double>("finops.chat.duration_ms", "ms", "Chat request duration");
    }
}
