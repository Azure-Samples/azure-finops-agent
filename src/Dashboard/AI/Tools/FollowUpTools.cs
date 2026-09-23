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

    [Description(@"Call after answering to suggest the next ACTION (1-3 clickable buttons). Call exactly once per turn (skip only at natural endpoints).

Rules:
1. Each follow-up MUST reference a concrete entity from this turn (resource, RG, service, file, $, region, window). Never generic.
2. The follow-up is the next ACTION, not a re-summary.
3. Each label ≤60 chars. Each prompt is a complete instruction the agent can execute.

## After data-heavy / uploaded-file turns: pass label2/label3 (and ideally label3) so user sees an action menu. FIRST action MUST be a deep prioritization. Use this exact pattern:

  label  = ""Rank top 5 actions by $ impact""
  prompt = ""Across all the data we just analyzed, deeply re-examine it and produce a ranked list of the 5 most impactful, actionable FinOps actions I should take. For each: (a) the concrete action in one sentence, (b) which file/resource/RG it applies to, (c) estimated $ saving (or risk if it's a governance action), (d) effort (low/med/high). Keep it short and decision-ready — a CFO should be able to read it in 30 seconds.""

For label2/label3 prefer (in order): GenerateScript on the #1 issue → GenerateHtmlPresentation → drill into top cost driver/RG/SKU → bulk PATCH tagging when Azure connected and tagging is the top finding.

## Small / single-question answers: just label/prompt.

Examples:
- After service breakdown: 'Drill into Virtual Machines (top spender at $58K)'
- After idle disks: 'Generate cleanup script for the 47 unattached disks in rg-data-eus2'
- After Crawl score: 'Score Walk maturity'
- After file analysis: 'Rank top 5 actions by $ impact' + 'Generate remediation script for the disks' + 'Build a CFO deck'")]
    private static string SuggestFollowUp(
        [Description("Short button label (max 60 chars), e.g. 'Drill into Virtual Machines (top spender)' or 'Rank top 5 actions by $ impact'")] string label,
        [Description("Full prompt sent when clicked \u2014 must be a complete, actionable instruction referencing concrete entities (RG, service, $ figure)")] string prompt,
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
