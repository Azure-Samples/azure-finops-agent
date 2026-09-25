using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Multi-signal idle/orphan resource detector built on top of Azure Resource Graph.
/// Runs canonical KQL queries that find the most common waste patterns and returns
/// a single structured report instead of forcing the LLM to stitch them together.
/// </summary>
public class IdleResourceTools
{
    private readonly UserTokens _tokens;

    public IdleResourceTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(FindIdleResources, "FindIdleResources",
            @"ONE-SHOT WASTE SCAN: runs a battery of Resource Graph KQL queries for common Azure waste patterns and returns a consolidated JSON report:
- Unattached managed disks
- Unassociated public IPs
- Stopped (not deallocated) VMs (still billed)
- Empty App Service plans
- Idle load balancers (no backend pool)
- Unused NICs
- Empty resource groups
- Old snapshots (>30 days)

DATA SCOPING: pass only the subscription IDs in the user's requested scope; omit them only for an explicitly all-accessible scan. Each pattern filters and projects at Resource Graph before applying topPerPattern (1-200). Use a small topPerPattern for a quick scan, not an estate-wide total; a limited pattern count is not the full count of matching resources. The tool always scans all eight patterns and has no resource-group, region or pattern selector. For one named pattern/resource group, use one scoped QueryAzure Resource Graph query with where, summarize/project and a result limit. Preserve all requested scope and disclose limited coverage; empty resource groups alone are not billable waste.

EVIDENCE LIMITS: inventory does not establish a monetary amount or billing currency. Do not turn zero matching candidates into an invented 0 USD/month estate-wide estimate. Empty resource groups and unused NICs are not billable on their own. Retain source freshness, requested scope and per-pattern limits.
SCRIPT DELIVERY: when the user explicitly requests a cleanup script, call GenerateScript in this turn rather than merely offering a future script. If no billable targets were found, still deliver a scoped, read-only revalidation/no-op script for review, with no mutation or invented resource targets: pass the returned host-built revalidationScript verbatim as GenerateScript scriptContent (bash) rather than authoring new KQL. Never execute it.
Use for 'find waste', 'orphaned resources', 'quick cost wins'.");
    }

    private async Task<string> FindIdleResources(
        [Description("Comma-separated subscription IDs from the requested scope. Omit only for an all-accessible scan, not when a subscription was named.")] string? subscriptionIds = null,
        [Description("Source-side limit per pattern, 1-200, default 50. Choose a small result for discovery and do not interpret limited rows as an estate-wide count. All eight patterns are still queried.")] int topPerPattern = 50)
    {
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", null, "idle");

        topPerPattern = Math.Clamp(topPerPattern, 1, 200);
        var subs = string.IsNullOrWhiteSpace(subscriptionIds)
            ? null
            : subscriptionIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();

        using var activity = HttpHelper.Telemetry.StartActivity("FindIdleResources");
        activity?.SetTag("idle.subs", subs is null ? "all" : string.Join(",", subs));
        activity?.SetTag("idle.top", topPerPattern);

        // Each entry: (label, KQL query). All queries are read-only (Resource Graph is read-only by design).
        var patterns = new (string Label, string Kql)[]
        {
            ("unattached_disks",
             $"Resources | where type =~ 'microsoft.compute/disks' | where managedBy == '' or isnull(managedBy) | project id, name, location, resourceGroup, sku=tostring(sku.name), sizeGB=toint(properties.diskSizeGB), state=tostring(properties.diskState) | order by sizeGB desc | top {topPerPattern} by sizeGB"),
            ("unassociated_public_ips",
             $"Resources | where type =~ 'microsoft.network/publicipaddresses' | where isnull(properties.ipConfiguration) and isnull(properties.natGateway) | project id, name, location, resourceGroup, sku=tostring(sku.name), tier=tostring(sku.tier) | top {topPerPattern} by name"),
            ("stopped_not_deallocated_vms",
             $"Resources | where type =~ 'microsoft.compute/virtualmachines' | extend pstates = properties.extended.instanceView.powerState.code | where pstates == 'PowerState/stopped' | project id, name, location, resourceGroup, vmSize=tostring(properties.hardwareProfile.vmSize), powerState=tostring(pstates) | top {topPerPattern} by name"),
            ("empty_app_service_plans",
             $"Resources | where type =~ 'microsoft.web/serverfarms' | extend sku=tostring(sku.name), tier=tostring(sku.tier), numberOfSites=toint(properties.numberOfSites) | where numberOfSites == 0 and tier !~ 'Free' and tier !~ 'Shared' | project id, name, location, resourceGroup, sku, tier | top {topPerPattern} by name"),
            ("unused_nics",
             $"Resources | where type =~ 'microsoft.network/networkinterfaces' | where isnull(properties.virtualMachine) and isnull(properties.privateEndpoint) | project id, name, location, resourceGroup | top {topPerPattern} by name"),
            ("idle_load_balancers",
             $"Resources | where type =~ 'microsoft.network/loadbalancers' | extend backendCount = array_length(properties.backendAddressPools) | where backendCount == 0 | project id, name, location, resourceGroup, sku=tostring(sku.name) | top {topPerPattern} by name"),
            ("old_snapshots",
             $"Resources | where type =~ 'microsoft.compute/snapshots' | extend created = todatetime(properties.timeCreated) | where created < ago(30d) | order by created asc | project id, name, location, resourceGroup, sizeGB=toint(properties.diskSizeGB), createdUtc=tostring(created) | take {topPerPattern}"),
            ("empty_resource_groups",
             $"ResourceContainers | where type =~ 'microsoft.resources/subscriptions/resourcegroups' | join kind=leftouter (Resources | summarize count() by resourceGroup, subscriptionId) on resourceGroup, subscriptionId | where isnull(count_) or count_ == 0 | project id, name, location, subscriptionId | top {topPerPattern} by name"),
        };

        // Fan out all 8 KQL queries in parallel. Each result is wrapped
        // in a per-query try/catch so a single failed pattern (perms,
        // throttle, schema drift) doesn't lose the other 7. Cuts wall
        // time from 8×latency to max(latency).
        var queryTasks = patterns.Select(async p =>
        {
            // Pass null activity — Activity is not safe for concurrent SetTag
            // writers across the 8 parallel queries. HttpHelper still emits
            // its own per-call span.
            try { return (p.Label, Result: await RunResourceGraphQuery(token, p.Kql, subs, activity: null)); }
            catch (HttpRequestException ex) { return (p.Label, Result: new { error = "query exception", detail = ex.Message }); }
            catch (TaskCanceledException ex) { return (p.Label, Result: new { error = "query exception", detail = ex.Message }); }
            catch (OperationCanceledException ex) { return (p.Label, Result: new { error = "query exception", detail = ex.Message }); }
        }).ToArray();
        var completed = await Task.WhenAll(queryTasks);
        var results = completed.ToDictionary(x => x.Label, x => x.Result);

        return JsonSerializer.Serialize(BuildReport(subs, topPerPattern, results),
            new JsonSerializerOptions { WriteIndented = true });
    }

    internal static object BuildReport(string[]? subscriptions, int topPerPattern, IReadOnlyDictionary<string, object> results) => new
    {
        generated_utc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        retrievedAtUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        dataAsOfUtc = (string?)null,
        freshness = "Resource Graph is an indexed inventory and may lag resource changes. Its source timestamp and indexing delay are unknown; retrievedAtUtc records completion of the scan, not source freshness.",
        subscriptions_scoped = subscriptions is null ? "all accessible" : string.Join(",", subscriptions),
        limitPerPattern = topPerPattern,
        countsAreEstateTotals = false,
        monthlyWaste = (decimal?)null,
        currency = (string?)null,
        note = "Monthly waste and currency are not provided by inventory. Price only matched billable candidates using their actual SKU, region, billing unit and commitment coverage; retail estimates are not verified net savings. With no candidates, say no waste was identified by these filters, not that the estate costs 0 USD/month. Empty resource groups and unused NICs alone are not billable waste.",
        scriptGuidance = "If a script was requested, call GenerateScript now. With no billable candidates, pass revalidationScript verbatim as scriptContent (language bash): it re-runs the scan's exact filters read-only, without mutations or invented targets. With candidates, reuse revalidationScript's queries to re-check targets before any reviewed action. A follow-up link is not the requested artifact. Never execute the script.",
        revalidationScript = BuildRevalidationScript(subscriptions),
        patterns = results
    };

    // Filters must match the scan patterns above so a revalidation re-checks exactly what was scanned.
    internal static readonly (string Label, string Filter)[] BillableRevalidationFilters =
    [
        ("Unattached managed disks", "Resources | where type =~ 'microsoft.compute/disks' | where managedBy == '' or isnull(managedBy)"),
        ("Unassociated public IP addresses", "Resources | where type =~ 'microsoft.network/publicipaddresses' | where isnull(properties.ipConfiguration) and isnull(properties.natGateway)"),
        ("Stopped but still allocated VMs", "Resources | where type =~ 'microsoft.compute/virtualmachines' | where tostring(properties.extended.instanceView.powerState.code) == 'PowerState/stopped'"),
        ("Empty paid App Service plans", "Resources | where type =~ 'microsoft.web/serverfarms' | where toint(properties.numberOfSites) == 0 and tostring(sku.tier) !~ 'Free' and tostring(sku.tier) !~ 'Shared'")
    ];

    internal static string BuildRevalidationScript(string[]? subscriptions)
    {
        var script = new StringBuilder();
        script.AppendLine("#!/usr/bin/env bash");
        script.AppendLine("# Read-only revalidation of idle-resource patterns (host-built from the FindIdleResources scan filters).");
        script.AppendLine("# Safety: READ-ONLY. Uses Azure Resource Graph queries only; it never deletes, stops, deallocates or modifies resources.");
        script.AppendLine("# Prerequisites: Azure CLI with the resource-graph extension, signed in with az login.");
        script.AppendLine("set -euo pipefail");
        script.AppendLine();
        var ids = (subscriptions ?? []).Where(s => Guid.TryParse(s, out _)).ToArray();
        if (ids.Length > 0)
            script.AppendLine($"SUBSCRIPTIONS=({string.Join(' ', ids.Select(s => "\"" + s + "\""))})");
        else
        {
            script.AppendLine("mapfile -t SUBSCRIPTIONS < <(az account list --query \"[?state=='Enabled'].id\" --output tsv)");
            script.AppendLine("[ \"${#SUBSCRIPTIONS[@]}\" -gt 0 ] || { echo \"ERROR: no enabled subscriptions are accessible.\" >&2; exit 1; }");
        }
        script.AppendLine("az extension add --name resource-graph --only-show-errors >/dev/null 2>&1 || az extension show --name resource-graph >/dev/null");
        script.AppendLine();
        script.AppendLine("check() {");
        script.AppendLine("  local label=\"$1\" kql=\"$2\" count");
        script.AppendLine("  count=$(az graph query --graph-query \"$kql | summarize resourceCount=count()\" --subscriptions \"${SUBSCRIPTIONS[@]}\" --query \"data[0].resourceCount\" --output tsv --only-show-errors) \\");
        script.AppendLine("    || { echo \"ERROR: Resource Graph query failed for: $label\" >&2; return 1; }");
        script.AppendLine("  [[ \"$count\" =~ ^[0-9]+$ ]] || { echo \"ERROR: missing or invalid count for: $label\" >&2; return 1; }");
        script.AppendLine("  echo \"$label: $count\"");
        script.AppendLine("  if [ \"$count\" -gt 0 ]; then");
        script.AppendLine("    az graph query --graph-query \"$kql | project id | take 1000\" --subscriptions \"${SUBSCRIPTIONS[@]}\" --query \"data[].id\" --output tsv --only-show-errors | sed 's/^/  /'");
        script.AppendLine("  fi");
        script.AppendLine("}");
        script.AppendLine();
        foreach (var (label, filter) in BillableRevalidationFilters)
            script.AppendLine($"check \"{label}\" \"{filter}\"");
        script.AppendLine();
        script.Append("echo \"Revalidation complete. No resources were changed.\"");
        return script.ToString();
    }

    private static async Task<object> RunResourceGraphQuery(string token, string kql, string[]? subs, System.Diagnostics.Activity? activity)
    {
        var options = new Dictionary<string, object> { ["resultFormat"] = "objectArray", ["$top"] = 1000 };
        var bodyObj = subs is null
            ? (object)new { query = kql, options }
            : new { subscriptions = subs, query = kql, options };
        var body = JsonSerializer.Serialize(bodyObj);

        var resp = await HttpHelper.SendWithRetryAsync(
            "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01",
            token, activity, "idle.rg",
            method: HttpMethod.Post, jsonBody: body);

        return ParseResourceGraphResponse(resp);
    }

    internal static object ParseResourceGraphResponse(string response)
    {
        if (!response.StartsWith("HTTP 200 ", StringComparison.Ordinal))
            return new { error = "query failed", detail = response[..Math.Min(response.Length, 400)] };

        var json = response[(response.IndexOf('\n') + 1)..];
        if (json.StartsWith("Current UTC time:")) json = json[(json.IndexOf('\n') + 1)..];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                && data.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object))
            {
                var count = data.GetArrayLength();
                var hasTruncation = doc.RootElement.TryGetProperty("resultTruncated", out var truncation)
                    && bool.TryParse(truncation.ToString(), out _);
                var truncated = hasTruncation && string.Equals(truncation.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                var hasContinuation = doc.RootElement.TryGetProperty("$skipToken", out var continuation)
                    && continuation.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(continuation.GetString());
                bool? complete = truncated || hasContinuation ? false : hasTruncation ? true : null;
                return new { count, items = data.Clone(), complete };
            }
            return new { error = "invalid inventory response", detail = "Resource Graph did not return an object-array data set; matching resources are unknown." };
        }
        catch (JsonException)
        {
            return new { error = "parse failed", detail = "Resource Graph returned invalid JSON; matching resources are unknown." };
        }
    }
}
