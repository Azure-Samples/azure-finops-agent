using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Marks a tool as <c>defer=Auto</c> so the Copilot CLI does NOT ship its full
/// schema in every LLM request — the model discovers it on demand via the CLI's
/// built-in tool search (by name or description) and it is loaded lazily.
///
/// Why: production telemetry (2026-07-11) showed EVERY LLM round-trip carrying
/// 26K+ input tokens on a fresh conversation — ~20K of which were the 19
/// always-on tool schemas (HtmlPresentationTools alone ~12K source chars).
/// Time-to-first-chunk was 3.5–34 s of the model chewing that payload.
/// Deferring the cold-path tools cuts the fixed per-request overhead by
/// roughly half; the hot path (QueryAzure, charts, follow-ups, scoring)
/// stays always-loaded for zero-latency access.
/// </summary>
public sealed class DeferredTool : DelegatingAIFunction
{
    private readonly IReadOnlyDictionary<string, object?> _props;

    private DeferredTool(AIFunction inner) : base(inner)
    {
        var merged = new Dictionary<string, object?>(inner.AdditionalProperties)
        {
            ["defer"] = GitHub.Copilot.CopilotToolDefer.Auto,
        };
        _props = merged;
    }

    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _props;

    public static AIFunction Wrap(AIFunction inner) => new DeferredTool(inner);

    public static IEnumerable<AIFunction> WrapAll(IEnumerable<AIFunction> tools)
        => tools.Select(Wrap);
}
