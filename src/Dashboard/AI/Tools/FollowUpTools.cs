using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public static class FollowUpTools
{
    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(SuggestFollowUp);
    }

    [Description(@"After fulfilling a tenant-data, remediation, Walk/Run, or uploaded-file request, optionally suggest the next ACTION (1-3 clickable buttons). At most one call per turn. Never substitute a follow-up offer for the deliverable already requested. For one simple next question, prefer a prompt link in the final answer instead of this extra tool round-trip. Use this tool for multiple actions or complex prompts; do not delay the primary answer just to create buttons.

Do not call after GetCrawlMaturityEvidence: it already supplies clickable actions. Do not call for standalone greetings, public/anonymous pricing or health, hypothetical estimates, or clarifications; use a prompt link in the answer instead. A missing consent action is emitted by the host, not a reason to recommend repeated generic reconnects.

DATA SCOPING: include only the concrete target, action and essential scope in each prompt. Reuse the current evidence without pasting tool responses, exports or the conversation transcript into button prompts. Make follow-up queries narrow enough to resolve one missing question; do not expand a resource-specific turn into a full tenant scan unless requested.

Rules:
1. Each follow-up MUST reference a concrete entity from this turn (resource, RG, service, file, $, region, window). Never generic.
2. The follow-up is the next ACTION, not a re-summary.
3. Each label ≤60 chars. Each prompt is a complete instruction the agent can execute.

After data-heavy / uploaded-file turns, optional second/third actions may expose a scoped script, report or unresolved decision. If prioritization was already requested, deliver it now rather than suggesting the same analysis again. Never offer an immediate Cost Management retry before its returned deadline.

## Structured actions: label/prompt are required; second/third pairs are optional.

Examples:
- After service breakdown: 'Drill into Virtual Machines (top spender at $58K)'
- After idle disks: 'Generate cleanup script for the 47 unattached disks in rg-data-eus2'
- After Walk score: 'Review the highest-impact evidenced fix'
- After file analysis: 'Rank top 5 actions by $ impact' + 'Generate remediation script for the disks' + 'Build a CFO deck'")]
    private static string SuggestFollowUp(
        [Description("Short button label (max 60 chars), e.g. 'Drill into Virtual Machines (top spender)' or 'Rank top 5 actions by $ impact'")] string label,
        [Description("Full prompt sent when clicked: one complete actionable instruction with the concrete target and essential scope. Do not paste tool responses or the conversation transcript into this prompt.")] string prompt,
        [Description("Optional second button label (\u226460 chars). Use after data-heavy / multi-file answers to surface a remediation script, CFO deck, or top-driver drill-down.")] string? label2 = null,
        [Description("Optional second prompt \u2014 paired with label2.")] string? prompt2 = null,
        [Description("Optional third button label (\u226460 chars).")] string? label3 = null,
        [Description("Optional third prompt \u2014 paired with label3.")] string? prompt3 = null)
    {
        var actions = new List<object> { new { label, prompt } };
        if (!string.IsNullOrWhiteSpace(label2) && !string.IsNullOrWhiteSpace(prompt2))
            actions.Add(new { label = label2, prompt = prompt2 });
        if (!string.IsNullOrWhiteSpace(label3) && !string.IsNullOrWhiteSpace(prompt3))
            actions.Add(new { label = label3, prompt = prompt3 });
        // Back-compat: keep the top-level label/prompt for older clients.
        return JsonSerializer.Serialize(new { label, prompt, actions });
    }
}
