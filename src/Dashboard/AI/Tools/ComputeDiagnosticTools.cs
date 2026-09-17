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
    private readonly Func<string, HttpMethod, string?, CancellationToken, Task<string>>? _send;

    internal ComputeDiagnosticTools(UserTokens tokens, Func<string, HttpMethod, string?, CancellationToken, Task<string>> send) : this(tokens)
        => _send = send;

    private Task<string> Send(string url, string telemetryPrefix, CancellationToken cancellationToken, HttpMethod? method = null, string? body = null)
        => _send?.Invoke(url, method ?? HttpMethod.Get, body, cancellationToken)
            ?? HttpHelper.SendWithRetryAsync(url, tokens.AzureToken!, null, telemetryPrefix, method, body,
                maxAttemptsOverride: 1, cancellationToken: cancellationToken);

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(CheckComputeFeasibility, "CheckComputeFeasibility",
            "Check deployment feasibility in Azure public cloud using subscription SKU restrictions, region/zone capabilities, correct Spot or standard quota, Spot placement scores and historical Spot eviction rates. Filter inputs to the subscriptions, exact SKU names and regions the user requested; do not fetch raw catalogues with QueryAzure for this check. The host filters matching catalogue records before retained-item limits and returns scoped evidence. All regions means every advertised location for the requested SKUs, not a hand-picked subset. Inspect catalogueCoverage and unknown results; an incomplete catalogue never proves unavailability. No resources are created. A high score, historical eviction rate or sufficient quota never guarantees allocation or uninterrupted runtime. Use this for where-can-I-deploy and Spot resilience questions, not retail-only price comparisons.");
        yield return AIFunctionFactory.Create(CheckVmConnectivity, "CheckVmConnectivity",
            "Run a bounded Network Watcher TCP connectivity diagnostic FROM the specified Azure VM. Requires an existing regional Network Watcher, agent extension, and delegated RBAC. Never creates or installs prerequisites. This is not a probe from the agent host. An accepted diagnostic is not yet a completed connectivity result.");
    }

    private async Task<string> CheckComputeFeasibility(
        [Description("JSON array of 1-10 subscription GUIDs from connection context.")] string subscriptionsJson,
        [Description("Filter to the exact VM SKU names requested, comma-separated, at most 5; do not request unrelated sizes.")] string skuNames,
        [Description("Filter to the requested Azure public regions, comma-separated, or 'all' for every advertised region. Never replace an all-regions request with a shortlist.")] string regions = "all",
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
        var catalogueCoverage = new List<object>();
        var history = priority == "spot" ? await ReadSpotEvictionHistoryAsync(subscriptions, sizes, allRegions ? [] : selectedRegions,
            (body, token) => Send("https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01",
                "compute.spot_history", token, HttpMethod.Post, body), cancellationToken) : null;
        var complete = history?.Complete ?? true;
        foreach (var subscription in subscriptions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var skuResponse = await ReadCollection($"/subscriptions/{subscription}/providers/Microsoft.Compute/skus?api-version=2021-07-01", cancellationToken,
                item => Text(item, "resourceType").Equals("virtualMachines", StringComparison.OrdinalIgnoreCase)
                    && sizes.Contains(Text(item, "name"), StringComparer.OrdinalIgnoreCase));
            complete &= skuResponse.Complete;
            var matchingSkus = skuResponse.Items;
            catalogueCoverage.Add(new
            {
                subscriptionId = subscription,
                status = skuResponse.Status,
                complete = skuResponse.Complete,
                pagesRead = skuResponse.PagesRead,
                itemsExamined = skuResponse.ItemsExamined,
                matchingRecords = matchingSkus.Length,
                incompleteReason = skuResponse.IncompleteReason
            });
            var locations = allRegions ? matchingSkus.SelectMany(item => Array(item, "locations").Select(value => value.GetString() ?? "")).Where(region => region.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : selectedRegions;
            if (locations.Length == 0)
            {
                complete = false;
                foreach (var size in sizes)
                    rows.Add(new
                    {
                        subscriptionId = subscription,
                        sku = size,
                        status = "unknown",
                        reason = skuResponse.Complete
                        ? "The completed catalogue did not advertise this SKU. New allocation eligibility is unverified; this does not disprove an existing deployment."
                        : "The catalogue read was incomplete. No conclusion about missing SKUs or regions is supported.",
                        skuListStatus = skuResponse.Status
                    });
                continue;
            }
            foreach (var location in locations)
            {
                var usage = await ReadCollection($"/subscriptions/{subscription}/providers/Microsoft.Compute/locations/{Uri.EscapeDataString(location)}/usages?api-version=2026-04-01", cancellationToken);
                complete &= usage.Complete;
                foreach (var size in sizes)
                {
                    var sku = FindRegionalSku(matchingSkus, size, location);
                    var result = Evaluate(sku, location, zone, usage.Items, desired, priority);
                    complete &= result.SkuStatus != "unknown" && result.QuotaStatus != "unknown";
                    var historyRows = history?.Rows.Where(row => Text(row, "sku").Equals(size, StringComparison.OrdinalIgnoreCase)
                        && Text(row, "region").Equals(location, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
                    var rate = historyRows.Length == 1 ? Text(historyRows[0], "evictionRate") : "";
                    if (priority == "spot") complete &= rate.Length > 0;
                    rows.Add(new
                    {
                        subscriptionId = subscription,
                        sku = size,
                        region = location,
                        zone,
                        result,
                        restrictions = Array(sku, "restrictions"),
                        skuListStatus = skuResponse.Status,
                        quotaStatus = usage.Status,
                        quotaComplete = usage.Complete,
                        quotaIncompleteReason = usage.IncompleteReason,
                        spotEvictionRate = rate.Length == 0 ? null : rate,
                        evictionHistoryStatus = priority != "spot" ? "not_requested" : rate.Length == 0 ? "unknown" : "reported"
                    });
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
                        result = new(await Send("https://management.azure.com" + path, "compute.placement", cancellationToken,
                            HttpMethod.Post, body), DateTimeOffset.UtcNow);
                        if (result.Response.StartsWith("HTTP 200 ", StringComparison.Ordinal))
                            PlacementCache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });
                    }
                    var coverage = GetPlacementCoverage(result!.Response, chunk, sizes, zone);
                    placement.Add(new
                    {
                        subscriptionId = subscription,
                        requestedRegions = chunk,
                        cacheStatus = cached ? "cached" : "queried",
                        retrievedAtUtc = result.RetrievedAtUtc,
                        complete = coverage.Complete,
                        status = coverage.Status,
                        expectedScores = coverage.ExpectedScores,
                        reportedScores = coverage.ReportedScores,
                        response = result.Response
                    });
                    complete &= coverage.Complete;
                }
            }
        }
        return JsonSerializer.Serialize(new
        {
            complete,
            catalogueCoverage,
            rows,
            spotPlacement = placement,
            retrievedAtUtc = DateTimeOffset.UtcNow,
            spotEvictionHistory = history is null ? null : new
            {
                source = "Azure Resource Graph SpotResources",
                status = history.Status,
                complete = history.Complete,
                queryComplete = history.QueryComplete,
                httpStatus = history.HttpStatus,
                totalRecords = history.TotalRecords,
                lookbackDays = 28,
                retrievedAtUtc = history.RetrievedAtUtc,
                sourceDataTimestamp = (string?)null,
                rows = history.Rows,
                guidance = "Historical regional eviction-rate bands are not a future guarantee or a zone-level prediction. Missing rows or rates are unknown, never zero. Retrieval time is not the observation time. Compare prices separately using the matching retail meter."
            },
            allocation = "not_attempted",
            policyValidation = "not_performed",
            guidance = "Report each evidence dimension separately. Unknown, incomplete or unattempted means unverified, never unavailable everywhere. "
                + "Catalogue permission, advertised Spot capability, quota, policy, historical deployments, prices, eviction history and current placement are distinct. "
                + "LowPriorityCapable is catalogue metadata, not proof about existing Spot VMs. A past deployment does not guarantee a new allocation. "
                + "No result guarantees placement or uninterrupted runtime. Spot scores are point-in-time recommendations, cached for 15 minutes; DataNotFound is unknown. "
                + "RestrictedSkuNotAvailable applies to the exact requested configuration now, not to past deployments. Policy was not validated."
        });
    }

    internal sealed record Feasibility(string SkuStatus, string QuotaStatus, int? RequiredVcpus, string[] EligibleZones, bool? LowPriorityCapable = null);

    internal sealed record PlacementCoverage(bool Complete, string Status, int ExpectedScores, int ReportedScores);

    internal static PlacementCoverage GetPlacementCoverage(string response, string[] regions, string[] sizes, string? zone)
    {
        var expected = regions.Length * sizes.Length;
        if (!response.StartsWith("HTTP 200 ", StringComparison.Ordinal)) return new(false, "unknown", expected, 0);
        using var document = JsonDocument.Parse(response[(response.IndexOf('\n') + 1)..]);
        var scores = Array(document.RootElement, "placementScores");
        var reported = 0;
        foreach (var region in regions)
            foreach (var size in sizes)
            {
                var matching = scores.Where(score => Text(score, "region").Equals(region, StringComparison.OrdinalIgnoreCase)
                    && Text(score, "sku").Equals(size, StringComparison.OrdinalIgnoreCase)
                    && Text(score, "availabilityZone") == (zone ?? "")).ToArray();
                if (matching.Length == 1 && Text(matching[0], "score") is "High" or "Medium" or "Low" or "RestrictedSkuNotAvailable")
                    reported++;
            }
        return new(reported == expected, reported == expected ? "reported" : reported == 0 ? "unknown" : "partial", expected, reported);
    }

    internal sealed record SpotHistory(string Status, bool Complete, bool QueryComplete, int HttpStatus, int? TotalRecords, JsonElement[] Rows, DateTimeOffset RetrievedAtUtc);

    internal static async Task<SpotHistory> ReadSpotEvictionHistoryAsync(string[] subscriptions, string[] sizes, string[] regions,
        Func<string, CancellationToken, Task<string>> send, CancellationToken cancellationToken)
    {
        var sizeFilter = string.Join(',', sizes.Select(size => $"'{size}'"));
        var regionFilter = regions.Length == 0 ? "" : $" | where location in~ ({string.Join(',', regions.Select(region => $"'{region}'"))})";
        var query = "SpotResources | where type =~ 'microsoft.compute/skuspotevictionrate/location'"
            + $" | where sku.name in~ ({sizeFilter})" + regionFilter
            + " | project sku=tostring(sku.name), region=tolower(location), evictionRate=tostring(properties.evictionRate) | order by sku asc, region asc";
        var body = JsonSerializer.Serialize(new
        {
            subscriptions = subscriptions.Distinct(StringComparer.OrdinalIgnoreCase),
            query,
            options = new Dictionary<string, object> { ["resultFormat"] = "objectArray", ["$top"] = 1000 }
        });
        var response = await send(body, cancellationToken);
        var retrievedAt = DateTimeOffset.UtcNow;
        var status = int.TryParse(response.Split(' ').ElementAtOrDefault(1), out var httpStatus) ? httpStatus : 0;
        if (!response.StartsWith("HTTP 200 ", StringComparison.Ordinal)) return new("unknown", false, false, status, null, [], retrievedAt);
        using var document = JsonDocument.Parse(response[(response.IndexOf('\n') + 1)..]);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new("unknown", false, false, status, null, [], retrievedAt);
        var rows = data.EnumerateArray().Take(1000).Select(item => item.Clone()).ToArray();
        int? total = root.TryGetProperty("totalRecords", out var totalProperty) && totalProperty.TryGetInt32(out var totalRecords) ? totalRecords : null;
        var queryComplete = data.GetArrayLength() <= 1000 && Text(root, "$skipToken").Length == 0
            && root.TryGetProperty("resultTruncated", out var truncated) && truncated.ToString().Equals("false", StringComparison.OrdinalIgnoreCase)
            && total == rows.Length;
        var complete = queryComplete && rows.Length > 0 && rows.All(row => Text(row, "evictionRate").Length > 0);
        return new(rows.Length == 0 ? "unknown" : complete ? "reported" : "partial", complete, queryComplete, status, total, rows, retrievedAt);
    }

    internal static JsonElement FindRegionalSku(IReadOnlyList<JsonElement> skus, string size, string region)
        => skus.FirstOrDefault(item => Text(item, "name").Equals(size, StringComparison.OrdinalIgnoreCase)
            && Array(item, "locations").Any(location => string.Equals(location.GetString(), region, StringComparison.OrdinalIgnoreCase)));

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
        var spotCapability = Array(sku, "capabilities").FirstOrDefault(item => Text(item, "name").Equals("LowPriorityCapable", StringComparison.OrdinalIgnoreCase));
        bool? lowPriorityCapable = bool.TryParse(Text(spotCapability, "value"), out var supported) ? supported : null;
        return new(blocked ? "blocked" : "permitted", quotaStatus, required, zones.Order().ToArray(), lowPriorityCapable);
    }

    private async Task<string> CheckVmConnectivity(
        [Description("ARM resource ID of an existing Network Watcher in the source VM's region; never create prerequisites.")] string networkWatcherResourceId,
        [Description("ARM resource ID of the existing source Azure VM from which to run the TCP diagnostic.")] string sourceVmResourceId,
        [Description("Exact destination hostname or IP address requested by the user; not a URL or an agent-host probe.")] string destinationAddress,
        [Description("Destination TCP port as a string, from 1 to 65535.")] string destinationPort,
        CancellationToken cancellationToken = default)
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

    internal sealed record CollectionRead(JsonElement[] Items, bool Complete, int Status, int PagesRead, int ItemsExamined, string? IncompleteReason);

    private Task<CollectionRead> ReadCollection(string path, CancellationToken cancellationToken, Func<JsonElement, bool>? include = null)
        => ReadCollectionAsync(path,
            (url, token) => Send(url, "compute.read", token),
            cancellationToken, include);

    internal static async Task<CollectionRead> ReadCollectionAsync(
        string path, Func<string, CancellationToken, Task<string>> send, CancellationToken cancellationToken, Func<JsonElement, bool>? include = null)
    {
        var items = new List<JsonElement>();
        var next = "https://management.azure.com" + path;
        var examined = 0;
        for (var page = 0; page < 30; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "management.azure.com" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
                return new(items.ToArray(), false, 0, page, examined, "invalid_next_link");
            var response = await send(next, cancellationToken);
            if (!response.StartsWith("HTTP 200 ", StringComparison.Ordinal))
                return new(items.ToArray(), false, int.TryParse(response.Split(' ').ElementAtOrDefault(1), out var status) ? status : 0, page + 1, examined, "http_failure");
            using var document = JsonDocument.Parse(response[(response.IndexOf('\n') + 1)..]);
            if (!document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                return new(items.ToArray(), false, 200, page + 1, examined, "invalid_collection_shape");
            foreach (var item in values.EnumerateArray())
            {
                examined++;
                if (!(include?.Invoke(item) ?? true)) continue;
                if (items.Count == 30000) return new(items.ToArray(), false, 200, page + 1, examined, "matching_item_limit");
                items.Add(item.Clone());
            }
            next = Text(document.RootElement, "nextLink");
            if (next.Length == 0) return new(items.ToArray(), true, 200, page + 1, examined, null);
        }
        return new(items.ToArray(), false, 200, 30, examined, "page_limit");
    }

    private static JsonElement[] Array(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
}