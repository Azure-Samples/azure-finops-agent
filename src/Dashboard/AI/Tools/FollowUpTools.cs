using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public static class FollowUpTools
{
    public static IEnumerable<AIFunction> Create() => [AIFunctionFactory.Create(SuggestFollowUp)];

    [Description("""
        Optionally offers 1-3 clickable next actions after a tenant-data, remediation or file-analysis answer. At most one call per turn.
        Call it only in the same response as another tool call that completes the answer (ReportMaturityScore, RenderChart, GenerateScript or GenerateDataReport), never in a response of its own: that costs a whole model round before the user sees the answer. Otherwise end the answer with [label](prompt:instruction) links, which render as the same buttons.
        Never substitute a follow-up offer for a deliverable the user already requested.
        Each action names a concrete entity from this turn (resource, resource group, service, amount, region or window) and is a complete instruction with its essential scope; labels are at most 60 characters.
        Pair every label with its own prompt: label describes prompt, label2 describes prompt2, label3 describes prompt3, and a label without its prompt is dropped.
        Skip it for greetings, public pricing, hypothetical estimates and clarifications. Never offer a Cost Management retry before its returned deadline.
        """)]
    private static string SuggestFollowUp(
        [Description("Button label, at most 60 characters. It must describe exactly the action its prompt performs: same entity, scope and analysis, promising nothing the prompt does not request.")] string label,
        [Description("Complete instruction sent when clicked, with the concrete target and scope. Do not paste tool output or the transcript.")] string prompt,
        [Description("Optional second label, describing exactly what prompt2 performs.")] string? label2 = null,
        [Description("Optional second prompt, paired with label2.")] string? prompt2 = null,
        [Description("Optional third label, describing exactly what prompt3 performs.")] string? label3 = null,
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