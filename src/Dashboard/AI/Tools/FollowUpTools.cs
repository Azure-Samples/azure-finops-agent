using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public static class FollowUpTools
{
    public static IEnumerable<AIFunction> Create() => [AIFunctionFactory.Create(SuggestFollowUp)];

    [Description("""
        Optionally offers 1-3 clickable next actions after a tenant-data, remediation or file-analysis answer. At most one call per turn.
        Never substitute a follow-up offer for a deliverable the user already requested. For a single simple next question, a prompt link in the answer is enough.
        Each action names a concrete entity from this turn (resource, resource group, service, amount, region or window) and is a complete instruction with its essential scope; labels are at most 60 characters.
        Skip it for greetings, public pricing, hypothetical estimates and clarifications. Never offer a Cost Management retry before its returned deadline.
        """)]
    private static string SuggestFollowUp(
        [Description("Button label, at most 60 characters.")] string label,
        [Description("Complete instruction sent when clicked, with the concrete target and scope. Do not paste tool output or the transcript.")] string prompt,
        [Description("Optional second label.")] string? label2 = null,
        [Description("Optional second prompt, paired with label2.")] string? prompt2 = null,
        [Description("Optional third label.")] string? label3 = null,
        [Description("Optional third prompt, paired with label3.")] string? prompt3 = null)
    {
        (string? Label, string? Prompt)[] pairs = [(label, prompt), (label2, prompt2), (label3, prompt3)];
        var actions = pairs
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Label) && !string.IsNullOrWhiteSpace(pair.Prompt))
            .Select(pair => new { label = pair.Label, prompt = pair.Prompt })
            .ToArray();
        // Top-level label/prompt remain for older clients.
        return JsonSerializer.Serialize(new { label, prompt, actions });
    }
}