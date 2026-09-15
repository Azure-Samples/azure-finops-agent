using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

namespace AzureFinOps.Dashboard.AI.Tools;

public sealed class ComputeDiagnosticTools(UserTokens tokens)
{
    private static readonly MemoryCache PlacementCache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private sealed record PlacementResult(string Response, DateTimeOffset RetrievedAtUtc);

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(CheckComputeFeasibility, "CheckComputeFeasibility",
            "Check deployment feasibility in Azure public cloud using subscription SKU restrictions, region/zone capabilities, correct Spot or standard quota, and optional Spot placement scores. All regions means every advertised location for the requested SKUs, not a hand-picked subset. No resources are created. A high score or sufficient quota never guarantees allocation. Use this for where-can-I-deploy questions, not retail-only price comparisons.");
        yield return AIFunctionFactory.Create(CheckVmConnectivity, "CheckVmConnectivity",
            "Run a bounded Network Watcher TCP connectivity diagnostic FROM the specified Azure VM. Requires an existing regional Network Watcher, agent extension, and delegated RBAC. Never creates or installs prerequisites. This is not a probe from the agent host. An accepted diagnostic is not yet a completed connectivity result.");
    }

    private async Task<string> CheckComputeFeasibility(
        [Description("JSON array of 1-10 subscription GUIDs from connection context.")] string subscriptionsJson,
        [Description("Comma-separated exact VM SKU names, at most 5.")] string skuNames,
        [Description("Comma-separated Azure public regions, or 'all'.")] string regions = "all",
        [Description("Requested VM count, 1-1000.")] string count = "1",
        [Description("'spot' or 'standard'.")] string priority = "standard",
        [Description("Optional zone number, 1, 2 or 3; omit for regional allocation.")] string? zone = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tokens.AzureToken)) return HttpHelper.TokenMissing("AzureToken", null, "compute");
        string[] subscriptions;
        try { subscriptions = JsonSerializer.Deserialize<string[]>(subscriptionsJson) ?? []; }
        catch (JsonException) { return "Error: subscriptionsJson must be a JSON string array."; }
        var sizes = skuNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var allRegions = regions.Equals("all", StringComparison.OrdinalIgnoreCase);
        var selectedRegions = regions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(region => region.ToLowerInvariant()).Distinct().ToArray();
        if (subscriptions.Length is < 1 or > 10 || subscriptions.Any(id => !Guid.TryParse(id, out _))
            || sizes.Length is < 1 or > 5 || sizes.Any(size => !Regex.IsMatch(size, "^[A-Za-z0-9_]{1,120}$"))
            || !int.TryParse(count, out var desired) || desired is < 1 or > 1000 || priority is not ("spot" or "standard")
            || zone is not (null or "" or "1" or "2" or "3") || !allRegions && (selectedRegions.Length is < 1 or > 100 || selectedRegions.Any(region => !Regex.IsMatch(region, "^[a-z0-9]{1,60}$"))))
            return "Error: invalid compute scope, SKU, count, priority or zone. Split requests exceeding the stated limits.";
        var rows = new List<object>();
        var placement = new List<object>();
        var complete = true;
        foreach (var subscription in subscriptions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var skuResponse = await ReadCollection($"/subscriptions/{subscription}/providers/Microsoft.Compute/skus?api-version=2021-07-01", cancellationToken);
            complete &= skuResponse.Complete;
            var matchingSkus = skuResponse.Items.Where(item => Text(item, "resourceType") == "virtualMachines" && sizes.Contains(Text(item, "name"), StringComparer.OrdinalIgnoreCase)).ToArray();
            var locations = allRegions ? matchingSkus.SelectMany(item => Array(item, "locations").Select(value => value.GetString() ?? "")).Where(region => region.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : selectedRegions;
            if (locations.Length == 0)
            {
                complete = false;
                rows.Add(new { subscriptionId = subscription, status = "unknown", reason = "No matching SKU locations were returned; availability was not established.", skuListStatus = skuResponse.Status });
                continue;
            }
            var quotaResults = new Dictionary<string, (JsonElement[] Items, bool Complete, int Status)>();
            foreach (var location in locations)
            {
                var usage = await ReadCollection($"/subscriptions/{subscription}/providers/Microsoft.Compute/locations/{Uri.EscapeDataString(location)}/usages?api-version=2026-04-01", cancellationToken);
                quotaResults[location] = usage;
                complete &= usage.Complete;
                foreach (var size in sizes)
                {
                    var sku = matchingSkus.FirstOrDefault(item => Text(item, "name").Equals(size, StringComparison.OrdinalIgnoreCase));
                    var result = Evaluate(sku, location, zone, usage.Items, desired, priority);
                    complete &= result.SkuStatus != "unknown" && result.QuotaStatus != "unknown";
                    rows.Add(new { subscriptionId = subscription, sku = size, region = location, zone, result, skuListStatus = skuResponse.Status, quotaStatus = usage.Status });
                }
            }
            if (priority == "spot")
            {
                foreach (var chunk in locations.Chunk(8))
                {
                    var body = JsonSerializer.Serialize(new { desiredLocations = chunk, desiredSizes = sizes.Select(size => new { sku = size }), desiredCount = desired, availabilityZones = !string.IsNullOrEmpty(zone) });
                    var path = $"/subscriptions/{subscription}/providers/Microsoft.Compute/locations/{Uri.EscapeDataString(chunk[0])}/placementScores/spot/generate?api-version=2025-06-05";
                    var key = CostQueryCoordinator.RequestKey(tokens.AzureToken, "POST", path, body);
                    var cached = PlacementCache.TryGetValue<PlacementResult>(key, out var result);
                    if (!cached)
                    {
                        result = new(await HttpHelper.SendWithRetryAsync("https://management.azure.com" + path, tokens.AzureToken, null, "compute.placement",
                            HttpMethod.Post, body, maxAttemptsOverride: 1, cancellationToken: cancellationToken), DateTimeOffset.UtcNow);
                        PlacementCache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });
                    }
                    placement.Add(new { subscriptionId = subscription, requestedRegions = chunk, cacheStatus = cached ? "cached" : "queried", retrievedAtUtc = result!.RetrievedAtUtc, response = result.Response });
                    complete &= result.Response.StartsWith("HTTP 200 ", StringComparison.Ordinal);
                }
            }
        }
        return JsonSerializer.Serialize(new
        {
            complete, rows, spotPlacement = placement, retrievedAtUtc = DateTimeOffset.UtcNow,
            allocation = "not_attempted", policyValidation = "not_performed",
            guidance = "SKU permission, quota, policy and allocation capacity are separate. No result guarantees placement. Report failed, missing and unattempted scopes explicitly. Spot scores are point-in-time evidence and cached for 15 minutes."
        });
    }

    internal sealed record Feasibility(string SkuStatus, string QuotaStatus, int? RequiredVcpus, string[] EligibleZones);

    internal static Feasibility Evaluate(JsonElement sku, string region, string? zone, IReadOnlyList<JsonElement> usages, int count, string priority)
    {
        if (sku.ValueKind != JsonValueKind.Object) return new("unknown", "unknown", null, []);
        var locations = Array(sku, "locations").Select(item => item.GetString() ?? "").ToArray();
        if (!locations.Contains(region, StringComparer.OrdinalIgnoreCase)) return new("not_available", "unknown", null, []);
        var info = Array(sku, "locationInfo").FirstOrDefault(item => Text(item, "location").Equals(region, StringComparison.OrdinalIgnoreCase));
        var zones = Array(info, "zones").Select(item => item.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        var blocked = false;
        foreach (var restriction in Array(sku, "restrictions"))
        {
            var type = Text(restriction, "type");
            var details = restriction.TryGetProperty("restrictionInfo", out var restrictionInfo) ? restrictionInfo : default;
            var restrictedLocations = Array(details, "locations").Select(item => item.GetString()).ToArray();
            if (restrictedLocations.Length > 0 && !restrictedLocations.Contains(region, StringComparer.OrdinalIgnoreCase)) continue;
            if (type.Equals("Location", StringComparison.OrdinalIgnoreCase))
            {
                var values = Array(restriction, "values").Select(item => item.GetString()).ToArray();
                if (values.Length == 0 || values.Contains(region, StringComparer.OrdinalIgnoreCase)) blocked = true;
            }
            else if (type.Equals("Zone", StringComparison.OrdinalIgnoreCase))
                foreach (var restrictedZone in Array(details, "zones")) zones.Remove(restrictedZone.GetString() ?? "");
            else blocked = true;
        }
        if (!string.IsNullOrEmpty(zone) && !zones.Contains(zone)) blocked = true;
        var capability = Array(sku, "capabilities").FirstOrDefault(item => Text(item, "name").Equals("vCPUs", StringComparison.OrdinalIgnoreCase));
        int? required = int.TryParse(Text(capability, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cores) && cores is > 0 and < 100000 ? cores * count : null;
        var quotaNames = priority == "spot" ? new[] { "lowPriorityCores" } : ["cores", Text(sku, "family")];
        var quotaStatus = required is null ? "unknown" : "sufficient";
        foreach (var name in quotaNames)
        {
            var quota = usages.FirstOrDefault(item => item.TryGetProperty("name", out var field) && Text(field, "value").Equals(name, StringComparison.OrdinalIgnoreCase));
            if (quota.ValueKind != JsonValueKind.Object || !quota.TryGetProperty("limit", out var limit) || !limit.TryGetDouble(out var maximum)
                || !quota.TryGetProperty("currentValue", out var current) || !current.TryGetDouble(out var used))
            { if (quotaStatus != "insufficient") quotaStatus = "unknown"; }
            else if (required is not null && maximum - used < required.Value) quotaStatus = "insufficient";
        }
        return new(blocked ? "blocked" : "permitted", quotaStatus, required, zones.Order().ToArray());
    }

    private async Task<string> CheckVmConnectivity(string networkWatcherResourceId, string sourceVmResourceId, string destinationAddress, string destinationPort, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tokens.AzureToken)) return HttpHelper.TokenMissing("AzureToken", null, "network");
        const string resourcePrefix = @"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/\\?#%]+/providers/";
        if (!Regex.IsMatch(networkWatcherResourceId, resourcePrefix + @"Microsoft\.Network/networkWatchers/[^/\\?#%]+$", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(sourceVmResourceId, resourcePrefix + @"Microsoft\.Compute/virtualMachines/[^/\\?#%]+$", RegexOptions.IgnoreCase)
            || Uri.CheckHostName(destinationAddress) == UriHostNameType.Unknown || !int.TryParse(destinationPort, out var port) || port is < 1 or > 65535)
            return "Error: provide valid Network Watcher and source VM resource IDs, destination host/IP, and TCP port.";
        var body = JsonSerializer.Serialize(new { source = new { resourceId = sourceVmResourceId }, destination = new { address = destinationAddress, port }, protocol = "Tcp", preferredIPVersion = "IPv4" });
        return await HttpHelper.SendWithRetryAsync("https://management.azure.com" + networkWatcherResourceId + "/connectivityCheck?api-version=2025-09-01",
            tokens.AzureToken, null, "network.connectivity", HttpMethod.Post, body, cancellationToken: cancellationToken);
    }

    private async Task<(JsonElement[] Items, bool Complete, int Status)> ReadCollection(string path, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var next = "https://management.azure.com" + path;
        for (var page = 0; page < 30; page++)
        {
            if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "management.azure.com" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
                return (items.ToArray(), false, 0);
            var response = await HttpHelper.SendWithRetryAsync(next, tokens.AzureToken!, null, "compute.read", maxAttemptsOverride: 1, cancellationToken: cancellationToken);
            if (!response.StartsWith("HTTP 200 ")) return (items.ToArray(), false, int.TryParse(response.Split(' ').ElementAtOrDefault(1), out var status) ? status : 0);
            using var document = JsonDocument.Parse(response[(response.IndexOf('\n') + 1)..]);
            items.AddRange(Array(document.RootElement, "value").Select(item => item.Clone()));
            if (items.Count > 30000) return (items.Take(30000).ToArray(), false, 200);
            next = Text(document.RootElement, "nextLink");
            if (next.Length == 0) return (items.ToArray(), true, 200);
        }
        return (items.ToArray(), false, 200);
    }

    private static JsonElement[] Array(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
}