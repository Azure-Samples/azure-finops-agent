using System.ComponentModel;
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

Use for 'find waste', 'orphaned resources', 'quick cost wins'. After calling, suggest GenerateScript for cleanup.");
    }

    private async Task<string> FindIdleResources(
        [Description("Optional comma-separated subscription IDs to scope the scan. Empty = all subscriptions the user has access to.")] string? subscriptionIds = null,
        [Description("Max resources per pattern (default 50, max 200).")] int topPerPattern = 50)
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
             $"Resources | where type =~ 'microsoft.compute/snapshots' | extend created = todatetime(properties.timeCreated) | where created < ago(30d) | project id, name, location, resourceGroup, sizeGB=toint(properties.diskSizeGB), createdUtc=tostring(created) | order by created asc | top {topPerPattern} by created asc"),
            ("empty_resource_groups",
             $"ResourceContainers | where type =~ 'microsoft.resources/subscriptions/resourcegroups' | join kind=leftouter (Resources | summarize count() by resourceGroup, subscriptionId) on resourceGroup, subscriptionId | where isnull(count_) or count_ == 0 | project id, name, location, subscriptionId | top {topPerPattern} by name"),
        };

        var results = new Dictionary<string, object>();
        foreach (var (label, kql) in patterns)
        {
            results[label] = await RunResourceGraphQuery(token, kql, subs, activity);
        }

        var summary = new
        {
            generated_utc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            subscriptions_scoped = subs is null ? "all accessible" : string.Join(",", subs),
            note = "Estimated monthly waste is NOT included — call GetAzureRetailPricing for each SKU type to compute exact $ savings, then GenerateScript for a cleanup script.",
            patterns = results,
        };

        return JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task<object> RunResourceGraphQuery(string token, string kql, string[]? subs, System.Diagnostics.Activity? activity)
    {
        var bodyObj = subs is null
            ? (object)new { query = kql, options = new { resultFormat = "objectArray", top = 1000 } }
            : new { subscriptions = subs, query = kql, options = new { resultFormat = "objectArray", top = 1000 } };
        var body = JsonSerializer.Serialize(bodyObj);

        var resp = await HttpHelper.SendWithRetryAsync(
            "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01",
            token, activity, "idle.rg",
            method: HttpMethod.Post, jsonBody: body);

        if (!resp.StartsWith("HTTP 200"))
            return new { error = "query failed", detail = resp[..Math.Min(resp.Length, 400)] };

        var json = resp[(resp.IndexOf('\n') + 1)..];
        if (json.StartsWith("Current UTC time:")) json = json[(json.IndexOf('\n') + 1)..];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                var count = data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : 0;
                return new { count, items = JsonSerializer.Deserialize<JsonElement>(data.GetRawText()) };
            }
            return new { count = 0, items = Array.Empty<object>() };
        }
        catch (Exception ex)
        {
            return new { error = "parse failed", detail = ex.Message };
        }
    }
}
