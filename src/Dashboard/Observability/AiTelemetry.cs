using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.Observability;

/// <summary>
/// Shared OpenTelemetry primitives + the app's per-user runtime state.
/// Created once at startup and threaded into every endpoint module so we
/// can keep <c>Program.cs</c> a thin composition root.
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

    /// <summary>
    /// All currently-live <see cref="CopilotSession"/> instances keyed by Copilot
    /// session id. A user can have multiple sessions on disk (the SDK persists each
    /// to <c>$COPILOT_HOME/session-state/{id}</c>) but only ones currently being
    /// chatted with live in memory. <see cref="CurrentSessionId"/> tracks which one
    /// is the user's "active" conversation.
    /// </summary>
    public ConcurrentDictionary<string, LiveSessionInfo> LiveSessions { get; } = new();

    /// <summary>userId → the sessionId the user is currently chatting in.</summary>
    public ConcurrentDictionary<long, string> CurrentSessionId { get; } = new();

    public ConcurrentDictionary<long, UserTokens> UserTokens { get; } = new();
    public ConcurrentDictionary<long, List<AIFunction>> UserTools { get; } = new();

    /// <summary>
    /// SDK-generated display titles keyed by sessionId, persisted across restarts.
    /// The Copilot CLI emits <c>session.title_changed</c> after a few turns; we
    /// override <see cref="SessionMetadata.Summary"/> in the sidebar with this so
    /// users see a concise generated label instead of their raw first prompt.
    /// </summary>
    public ConcurrentDictionary<string, string> SessionTitles { get; } = new();

    private static readonly string TitlesFile = Path.Combine(
        Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(Path.GetTempPath(), "copilot"),
        "session-titles.json");

    private readonly object _titlesLock = new();

    public void LoadTitles()
    {
        try
        {
            if (!File.Exists(TitlesFile)) return;
            var json = File.ReadAllText(TitlesFile);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is null) return;
            foreach (var kv in dict) SessionTitles[kv.Key] = kv.Value;
        }
        catch { /* ignore corrupt/missing — titles are best-effort */ }
    }

    public void SaveTitle(string sessionId, string title)
    {
        SessionTitles[sessionId] = title;
        PersistTitles();
    }

    /// <summary>Removes a title from the in-memory dict and the on-disk JSON.
    /// Called from every session-delete path so the file (and dictionary) don't
    /// leak entries forever.</summary>
    public void RemoveTitle(string sessionId)
    {
        if (SessionTitles.TryRemove(sessionId, out _))
            PersistTitles();
    }

    private void PersistTitles()
    {
        lock (_titlesLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TitlesFile)!);
                File.WriteAllText(TitlesFile, JsonSerializer.Serialize(SessionTitles));
            }
            catch { /* best-effort */ }
        }
    }

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

/// <summary>
/// Bundles a live <see cref="CopilotSession"/> with the metadata we need to
/// recycle it when the BYOK bearer token bakes-in expires, and to associate it
/// back with the user who owns it (for janitor cleanup).
/// </summary>
public sealed class LiveSessionInfo
{
    public required CopilotSession Session { get; init; }
    public required long UserId { get; init; }
    public required DateTimeOffset BearerExpiry { get; set; }
}
