using System.Text.Json.Serialization;

namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// One event in a conversation's turn stream. Streaming deltas and usage are
/// live-only; the remaining events are appended to the conversation's local
/// transcript so the UI can replay it without reading the model service.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserMessageEvent), "user")]
[JsonDerivedType(typeof(AssistantMessageEvent), "assistant")]
[JsonDerivedType(typeof(ReasoningEvent), "thinking")]
[JsonDerivedType(typeof(ToolStartEvent), "tool_start")]
[JsonDerivedType(typeof(ToolCompleteEvent), "tool_done")]
[JsonDerivedType(typeof(ApprovalRequestEvent), "approval_request")]
[JsonDerivedType(typeof(TurnErrorEvent), "error")]
[JsonDerivedType(typeof(TurnIdleEvent), "idle")]
[JsonDerivedType(typeof(AnswerFeedbackEvent), "feedback")]
public abstract record AgentEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public virtual bool Persisted => true;
}

public sealed record UserMessageEvent(string Content) : AgentEvent;

public sealed record MessageDeltaEvent(string MessageId, string Content) : AgentEvent
{
    public override bool Persisted => false;
}

public sealed record ReasoningDeltaEvent(string Content) : AgentEvent
{
    public override bool Persisted => false;
}

/// <summary>The model's reasoning summary that preceded the next tool call or answer, kept so a reopened conversation still shows it.</summary>
public sealed record ReasoningEvent(string Content) : AgentEvent;

public sealed record AssistantMessageEvent(string MessageId, string Content) : AgentEvent;

public sealed record UsageEvent(long? InputTokens, long? OutputTokens, string? FinishReason) : AgentEvent
{
    public override bool Persisted => false;
}

public sealed record ToolStartEvent(string CallId, string ToolName, string? Arguments) : AgentEvent;

public sealed record ToolCompleteEvent(string CallId, bool Success, string? Result, string? Error, long? DurationMs = null) : AgentEvent;

/// <summary>Agent Framework paused the turn until the user approves or rejects this exact tool call.</summary>
public sealed record ApprovalRequestEvent(string RequestId, string ToolName, string? Arguments) : AgentEvent;

/// <summary>The user's answer to a pending <see cref="ApprovalRequestEvent"/>.</summary>
public sealed record ApprovalDecision(string RequestId, bool Approved);

public sealed record TurnErrorEvent(string Message, string? Code = null) : AgentEvent;

public sealed record TurnIdleEvent : AgentEvent;

/// <summary>
/// The owner's rating of the answer to their <paramref name="Turn"/>-th visible question:
/// "up", "down", or "none" to clear it. The latest rating for a turn wins.
/// </summary>
public sealed record AnswerFeedbackEvent(int Turn, string Rating) : AgentEvent;

/// <summary>Listing entry for one locally indexed conversation.</summary>
public sealed record AgentSessionInfo(string SessionId, DateTimeOffset StartTime, DateTimeOffset ModifiedTime, string? Summary, string WorkingDirectory);

/// <summary>An image attached natively to a user message.</summary>
public sealed record ImageAttachment(string Path, string MimeType, string DisplayName);
