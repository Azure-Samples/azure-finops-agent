using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Generates downloadable Azure CLI / PowerShell scripts and Bicep / ARM templates from FinOps recommendations.
/// The LLM produces the content after discussing with the user, and this tool packages it as a
/// downloadable .sh, .ps1, .bicep or .json file with a code preview in the UI. It never runs or deploys it.
/// </summary>
public sealed class ScriptTools(long ownerUserId)
{
    internal static void CleanupOldFiles() => ArtifactStore.Default.Cleanup();

    private static readonly Dictionary<string, (string Extension, string ContentType)> Languages = new(StringComparer.Ordinal)
    {
        ["bash"] = (".sh", "application/x-shellscript"),
        ["powershell"] = (".ps1", "application/x-powershell"),
        ["bicep"] = (".bicep", "text/plain"),
        ["arm"] = (".json", "application/json"),
    };

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GenerateScript, "GenerateScript",
            @"Directly creates a downloadable Azure CLI or PowerShell script, or a Bicep or ARM deployment template, from the complete code supplied in scriptContent. The tool packages that code as a file; it never executes or deploys it and does not write or complete the code itself.

Call this tool in the same response whenever the user explicitly requests code, a script, a template, or a repeatable command workflow. Write standard Azure CLI and PowerShell commands from knowledge, without searching documentation or the web for their syntax first: the user reviews the script before running it. Write the complete content in scriptContent instead of returning only a fenced code block. When the user asks for an ARM template or a Bicep file, deliver that template itself (language arm or bicep), never a script that writes it. For tenant-specific remediation, analyze and scope the environment first. A self-contained parameterized script does not require a tenant query.

If the requested tenant-specific script depends on targets that have not been identified yet, query only the filtered evidence needed to identify them before calling this tool.

For Azure Resource Graph, use az graph query --graph-query (or -q) for KQL; --query is only the JMESPath selector for the returned JSON, not the KQL argument. Never emit duplicate --query flags. Its JSON output is {count, data, skip_token, total_records} in snake_case, unlike the REST API's totalRecords and $skipToken: read rows from .data and counts from .count or .total_records. With -o tsv, a JMESPath list ([a, b]) prints one value per line while an object ({a:a, b:b}) prints its values on one tab-separated line, so read several fields with one read from an object query: read -r a b < <(az ... --query '{a:a, b:b}' -o tsv). Keep command failures and invalid/missing output as explicit errors: a missing count is unknown, never zero.

For scripts that change resources, default to dry-run and require explicit local confirmation. Confirmation must actually reach the user: inside a loop that reads candidates from standard input (while read ... done < file or < <(command)), read the answer from the terminal (read -r answer < /dev/tty) or loop over an array instead, because a plain read there consumes the next candidate and the change can never be confirmed. A requested cleanup or remediation script contains the actual change commands for the identified targets behind that guard; an inventory-only script does not fulfil it. When the evidence found no targets now, the script still re-queries each requested category when run and applies those guarded change commands to whatever it finds, rather than being read-only. Use only preview flags supported by the chosen commands; do not invent a universal --what-if flag. Never embed credentials; use the user's own login, managed identity, or local secret input. Query scripts must filter and aggregate at the source with supported API options instead of downloading full collections for a summary. Prefer Azure CLI (`az`) unless user asks for PowerShell.

Templates describe resources for the user to review and deploy themselves: build them from the resource properties already read this conversation, parameterize names, locations and SKUs, mark secrets as secure parameters without default values, and state the deploy and what-if commands (az deployment group what-if / create) in your answer. An ARM template is one JSON object with $schema, contentVersion and resources.");
    }

    private Task<string> GenerateScript(
        [Description(@"The complete executable script, or the complete template. Supply the actual code, not instructions asking the tool to generate it. A script (Azure CLI or PowerShell) must include:
- A header comment block explaining what the script does, prerequisites, and usage
- For changes: dry-run by default and local confirmation, using only supported preview flags; a confirmation read inside a loop that reads its candidates from standard input reads from the terminal (read -r answer < /dev/tty)
- No embedded credentials; use the user's login, managed identity, or local secret input
- For queries: source-side filtering and aggregation with supported API options
- For az graph query: KQL goes in --graph-query/-q; --query only selects the returned JSON with JMESPath
- Clear comments for each logical section
- Error handling for critical operations; never replace failed queries or invalid/missing counts with zero
Example header:
#!/bin/bash
# FinOps Remediation Script: Delete Orphaned Disks
# Generated by Azure FinOps Agent
# Prerequisites: Azure CLI 2.50+, logged in via 'az login'
# Usage: chmod +x script.sh && ./script.sh
# Mode: DRY-RUN by default — set DRY_RUN=false to execute
A template is the complete Bicep file, or the complete ARM template JSON object (with $schema, contentVersion and resources; describe it in metadata), with no embedded credentials.")] string scriptContent,
        [Description("Filename without extension. Default: 'finops-remediation'")] string? filename = null,
        [Description("'bash' for an Azure CLI .sh script, 'powershell' for .ps1, 'bicep' for a .bicep template, 'arm' for an ARM template .json. Default: 'bash'")] string? language = null,
        [Description("Optional brief description of what the file does (shown in the UI download button)")] string? description = null)
    {
        if (string.IsNullOrWhiteSpace(scriptContent))
            return Task.FromResult("Error: No script content provided.");

        var lang = (language ?? "bash").Trim().ToLowerInvariant();
        if (!Languages.TryGetValue(lang, out var format))
            (lang, format) = ("bash", Languages["bash"]);
        if (lang == "arm" && ArmTemplateError(scriptContent) is { } error)
            return Task.FromResult($"Error: {error}");

        CleanupOldFiles();

        var safeName = string.IsNullOrWhiteSpace(filename) ? "finops-remediation" : SanitizeFilename(filename);
        var desc = string.IsNullOrWhiteSpace(description) ? lang is "arm" or "bicep" ? "FinOps deployment template" : "FinOps remediation script" : description;

        var artifact = ArtifactStore.Default.Register(ownerUserId, safeName + format.Extension, format.ContentType, Encoding.UTF8.GetBytes(scriptContent));

        var lineCount = scriptContent.Split('\n').Length;

        return Task.FromResult($"__SCRIPT_READY__:{artifact.Id}:{safeName}{format.Extension}:{lineCount}:{lang}:{desc}");
    }

    // An ARM template file must be one deployable JSON object; comments and trailing commas are accepted as Azure tooling does.
    private static string? ArmTemplateError(string content)
    {
        try
        {
            using var template = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = template.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$schema", out _) && root.TryGetProperty("contentVersion", out _)
                && root.TryGetProperty("resources", out _)
                ? null
                : "An ARM template is one JSON object with $schema, contentVersion and resources.";
        }
        catch (JsonException exception)
        {
            return $"The ARM template is not valid JSON ({exception.Message}).";
        }
    }

    private static string SanitizeFilename(string name) =>
        TempFileHelper.SanitizeFilename(name, "finops-remediation");
}
