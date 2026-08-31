using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Collects the seven Crawl-level FinOps evidence sets in one agent tool call.
/// The underlying ARM reads still fan out, but they do so server-side instead
/// of forcing one model round-trip per API batch.
/// </summary>
public sealed class CrawlMaturityTools
{
    private readonly UserTokens _tokens;
    private readonly ScoreTools _scoreTools;

    public CrawlMaturityTools(UserTokens tokens, ScoreTools scoreTools)
    {
        _tokens = tokens;
        _scoreTools = scoreTools;
    }

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GetCrawlMaturityEvidence, "GetCrawlMaturityEvidence", @"Collects, scores, and persists all seven Crawl maturity dimensions in ONE tool call: budgets/current spend, exact CostCenter/Owner/Environment tagging, exports, alerts/scheduled actions, policy guardrails, common waste, and cost visibility. It also returns ready-to-render fix actions. Low-cost metadata reads run with bounded server-side concurrency; no Cost Management /query is needed because budget currentSpend provides exact MTD spend.
    Use exactly once for Crawl/FinOps maturity scoring. Pass the exact `subscriptions` array and optional first management-group id from the connection context. Do NOT supplement it with QueryAzure, ReportMaturityScore, SuggestFollowUp, or any other tool—the score persistence, maturity SSE event, and follow-up buttons are already handled by this result.");
    }

    private async Task<string> GetCrawlMaturityEvidence(
        [Description("Exact subscriptions JSON array from the connection context, with id and name fields")] string subscriptionsJson,
        [Description("Optional management-group id or full ARM path from the connection context")] string? managementGroupId = null)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", null, "crawl");

        var (subscriptions, parseError) = ParseSubscriptions(subscriptionsJson);
        if (parseError is not null) return $"HTTP 400 BadRequest\n{parseError}";
        if (subscriptions.Count == 0) return "HTTP 400 BadRequest\nNo valid subscription IDs were supplied.";

        var ids = subscriptions.Select(s => s.Id).ToArray();

        const string taggingQuery =
            "resources | extend ccValue=tolower(tostring(tags['CostCenter'])), ownerValue=tolower(tostring(tags['Owner'])), envValue=tolower(tostring(tags['Environment'])) | extend cc=iff(isnotempty(ccValue) and ccValue !in ('unassigned','unknown','n/a','none','tbd','-'),1,0), own=iff(isnotempty(ownerValue) and ownerValue !in ('unassigned','unknown','n/a','none','tbd','-'),1,0), env=iff(isnotempty(envValue) and envValue !in ('unassigned','unknown','n/a','none','tbd','-'),1,0), ccPlaceholder=iff(ccValue in ('unassigned','unknown','n/a','none','tbd','-'),1,0), ownPlaceholder=iff(ownerValue in ('unassigned','unknown','n/a','none','tbd','-'),1,0), envPlaceholder=iff(envValue in ('unassigned','unknown','n/a','none','tbd','-'),1,0), hasDeptLower=iff(isnotempty(tostring(tags['department'])),1,0), hasDeptUpper=iff(isnotempty(tostring(tags['Department'])),1,0) | extend governed=iff(cc==1 and own==1 and env==1,1,0) | summarize total=count(), costCenter=sum(cc), owner=sum(own), environment=sum(env), fullyTagged=sum(governed), placeholders=sum(ccPlaceholder)+sum(ownPlaceholder)+sum(envPlaceholder), departmentLower=sum(hasDeptLower), departmentUpper=sum(hasDeptUpper) by subscriptionId | project subscriptionId, total, costCenter, owner, environment, fullyTagged, placeholders, departmentLower, departmentUpper | order by total desc";
        const string policyQuery =
            "policyresources | where type =~ 'microsoft.authorization/policyassignments' | extend raw=tolower(tostring(properties)) | extend finops=raw has_any ('tag','cost','allowedlocations','allowedskus','budget') | summarize totalAssignments=count(), finOpsAssignments=countif(finops) by subscriptionId | project subscriptionId, totalAssignments, finOpsAssignments | order by totalAssignments desc";
        const string wasteQuery =
            "resources | extend wasteType=case(type =~ 'microsoft.compute/disks' and (isempty(tostring(managedBy)) or tostring(properties.diskState) =~ 'Unattached'),'Unattached disk', type =~ 'microsoft.network/publicipaddresses' and isempty(tostring(properties.ipConfiguration.id)) and isempty(tostring(properties.natGateway.id)),'Orphaned public IP', type =~ 'microsoft.web/serverfarms' and toint(properties.numberOfSites)==0 and tostring(sku.tier) !~ 'Free' and tostring(sku.tier) !~ 'Shared','Empty App Service plan','') | where wasteType != '' | summarize wasteCount=count(), names=make_set(name,10) by subscriptionId, wasteType";
        const string emptyResourceGroupsQuery =
            "resourcecontainers | where type =~ 'microsoft.resources/subscriptions/resourcegroups' | project subscriptionId, resourceGroup=name | join kind=leftouter (resources | summarize resourceCount=count() by subscriptionId, resourceGroup) on subscriptionId, resourceGroup | extend resourceCount=coalesce(resourceCount,0) | where resourceCount==0 | summarize emptyGroupCount=count(), names=make_set(resourceGroup,10) by subscriptionId";

        using var requestLimiter = new SemaphoreSlim(12, 12);
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentMonthStart = new DateOnly(utcToday.Year, utcToday.Month, 1);

        var taggingTask = RunResourceGraph(token, ids, taggingQuery, "crawl.tagging", requestLimiter);
        var policyTask = RunResourceGraph(token, ids, policyQuery, "crawl.policy", requestLimiter);
        var wasteTask = RunResourceGraph(token, ids, wasteQuery, "crawl.waste", requestLimiter);
        var emptyGroupsTask = RunResourceGraph(token, ids, emptyResourceGroupsQuery, "crawl.empty_groups", requestLimiter);

        var budgetTask = Task.WhenAll(subscriptions.Select(async s =>
            (Scope: s, Response: await ReadArmCollection(
                token,
                $"/subscriptions/{s.Id}/providers/Microsoft.Consumption/budgets?api-version=2024-08-01",
            "crawl.budgets",
            requestLimiter))));
        var exportsTask = ReadCollections(token, subscriptions, "exports", "crawl.exports", requestLimiter);
        // scheduledActions has not adopted the newer general Cost Management
        // API version; ARM returns UnsupportedApiVersion for 2026-08-01.
        var actionsTask = ReadCollections(token, subscriptions, "scheduledActions", "crawl.scheduled_actions", requestLimiter, "2025-03-01");
        var alertsTask = ReadCollections(token, subscriptions, "alerts", "crawl.alerts", requestLimiter);

        await Task.WhenAll(taggingTask, policyTask, wasteTask, emptyGroupsTask,
            budgetTask, exportsTask, actionsTask, alertsTask);
        var apiMs = totalSw.ElapsedMilliseconds;

        var budgets = budgetTask.Result
            .Select(x => CompactBudget(x.Scope, x.Response, currentMonthStart, utcToday))
            .ToList();
        var currencies = budgets
            .Where(b => b.CurrentSpend is not null)
            .Select(b => b.Currency)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var totalsByCurrency = budgets
            .Where(b => b.CurrentSpend is not null && !string.IsNullOrWhiteSpace(b.Currency))
            .GroupBy(b => b.Currency!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(b => b.CurrentSpend!.Value), 6), StringComparer.OrdinalIgnoreCase);
        var mtdFromBudgets = currencies.Length == 1
            ? totalsByCurrency[currencies[0]]
            : (double?)null;
        // A total built from only some subscriptions must never be narrated as
        // the estate MTD, so publish the coverage alongside every spend field.
        var subscriptionsWithValidatedSpend = budgets.Count(b => b.CurrentSpend is not null);
        var spendComplete = subscriptionsWithValidatedSpend == subscriptions.Count;

        var evidence = new
        {
            generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            subscriptionCount = subscriptions.Count,
            subscriptions,
            budgets = new
            {
                subscriptionsWithBudgets = budgets.Count(b => b.BudgetCount > 0),
                totalBudgets = budgets.Sum(b => b.BudgetCount),
                mtdCurrentSpend = mtdFromBudgets is null ? (double?)null : Math.Round(mtdFromBudgets.Value, 2),
                spendComplete,
                subscriptionsWithValidatedSpend,
                currency = currencies.Length == 1 ? currencies[0] : null,
                totalsByCurrency,
                details = budgets
            },
            tagging = taggingTask.Result,
            exports = exportsTask.Result,
            scheduledActions = actionsTask.Result,
            alerts = alertsTask.Result,
            policy = policyTask.Result,
            waste = new
            {
                commonPatterns = wasteTask.Result,
                emptyResourceGroups = emptyGroupsTask.Result
            },
            visibility = new
            {
                mtdSource = "Microsoft.Consumption budgets currentSpend",
                mtdCurrentSpend = mtdFromBudgets is null ? (double?)null : Math.Round(mtdFromBudgets.Value, 2),
                spendComplete,
                currency = currencies.Length == 1 ? currencies[0] : null,
                totalsByCurrency,
                subscriptionsWithValidatedSpend,
                ownershipSignal = "CostCenter + Owner + Environment tag coverage"
            }
        };

        var scores = BuildScores(
            subscriptions,
            budgets,
            taggingTask.Result,
            exportsTask.Result,
            actionsTask.Result,
            alertsTask.Result,
            policyTask.Result,
            wasteTask.Result,
            emptyGroupsTask.Result,
            mtdFromBudgets,
            currencies.Length == 1 ? currencies[0] : null,
            totalsByCurrency);
        var scoreJson = JsonSerializer.Serialize(scores);
        _scoreTools.SaveScore("crawl", scoreJson);

        var emptyGroups = DataRows(emptyGroupsTask.Result);
        var emptyGroupCount = emptyGroups.Sum(r => IntProperty(r, "emptyGroupCount"));
        var emptyGroupNames = emptyGroups
            .SelectMany(r => StringArrayProperty(r, "names"))
            .Take(3)
            .ToArray();
        var firstActionPrompt =
            $"Review existing valid CostCenter, Owner, and Environment values, then apply missing tags consistently across {subscriptions.Count} subscriptions; configure missing daily exports and anomaly alerts. Ask before any write, use bulk operations, never invent placeholder tag values, do not delete resources, and summarize changes in one line.";
        var followUpActions = new[]
        {
            new { label = "Auto-fix tags + exports + alerts", prompt = firstActionPrompt },
            new { label = "Re-score Crawl maturity", prompt = "Re-score my Crawl FinOps maturity across all connected subscriptions and compare it with the prior score." },
            new
            {
                label = $"Review {emptyGroupCount} empty resource groups",
                prompt = $"Review the {emptyGroupCount} empty resource groups, including {string.Join(", ", emptyGroupNames)}, and generate a dry-run cleanup script with confirmations; do not delete anything."
            }
        };
        var followUp = new
        {
            label = followUpActions[0].label,
            prompt = followUpActions[0].prompt,
            actions = followUpActions
        };

        return JsonSerializer.Serialize(new
        {
            kind = "crawl_maturity_result",
            scores,
            followUp,
            evidence,
            diagnostics = new
            {
                apiMs,
                totalToolMs = totalSw.ElapsedMilliseconds
            }
        });
    }

    private async Task<IReadOnlyList<CollectionEvidence>> ReadCollections(
        string token,
        IReadOnlyList<SubscriptionScope> subscriptions,
        string collection,
        string telemetryPrefix,
        SemaphoreSlim requestLimiter,
        string apiVersion = "2026-08-01")
    {
        var tasks = subscriptions.Select(async s =>
        {
            var response = await ReadArmCollection(
                token,
                $"/subscriptions/{s.Id}/providers/Microsoft.CostManagement/{collection}?api-version={apiVersion}",
                telemetryPrefix,
                requestLimiter);
            return CompactCollection(s, response);
        });
        return await Task.WhenAll(tasks);
    }

    private static async Task<string> SendArm(
        string token,
        string path,
        string telemetryPrefix,
        SemaphoreSlim requestLimiter)
    {
        var url = path.StartsWith("https://management.azure.com/", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"https://management.azure.com{path}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase))
            return "HTTP 400 BadRequest\nInvalid ARM collection continuation URL.";

        await requestLimiter.WaitAsync();
        try
        {
            return await HttpHelper.SendWithRetryAsync(
                uri.AbsoluteUri,
                token, null, telemetryPrefix,
                bypassCostManagementGate: true,
                maxAttemptsOverride: 1);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"HTTP 0 TransportError\n{Truncate(ex.Message, 300)}";
        }
        finally
        {
            requestLimiter.Release();
        }
    }

    private static async Task<string> ReadArmCollection(
        string token,
        string initialPath,
        string telemetryPrefix,
        SemaphoreSlim requestLimiter)
    {
        const int maxPages = 20;
        const int maxItems = 5000;
        var values = new List<JsonElement>();
        var next = initialPath;

        for (var page = 0; page < maxPages; page++)
        {
            var response = await SendArm(token, next, telemetryPrefix, requestLimiter);
            if (ParseStatus(response) != 200) return response;

            try
            {
                using var doc = JsonDocument.Parse(ResponseBody(response));
                if (!doc.RootElement.TryGetProperty("value", out var pageValues)
                    || pageValues.ValueKind != JsonValueKind.Array)
                    return "HTTP 0 ParseError\nARM collection response did not contain a value array.";

                values.AddRange(pageValues.EnumerateArray().Select(item => item.Clone()));
                if (values.Count > maxItems)
                    return $"HTTP 206 PartialContent\nARM collection exceeded the {maxItems}-item safety limit.";

                next = doc.RootElement.TryGetProperty("nextLink", out var nextLink)
                    ? nextLink.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(next))
                {
                    var body = JsonSerializer.Serialize(new { value = values });
                    return $"HTTP 200 OK\n{body}";
                }
            }
            catch (JsonException ex)
            {
                return $"HTTP 0 ParseError\n{Truncate(ex.Message, 300)}";
            }
        }

        return $"HTTP 206 PartialContent\nARM collection exceeded the {maxPages}-page safety limit.";
    }

    private static async Task<object> RunResourceGraph(
        string token,
        string[] subscriptions,
        string query,
        string telemetryPrefix,
        SemaphoreSlim requestLimiter)
    {
        const int maxRows = 5000;
        var rows = new List<JsonElement>();
        string? skipToken = null;

        for (var page = 0; page < 10; page++)
        {
            var options = new Dictionary<string, object?>
            {
                ["resultFormat"] = "objectArray",
                ["$top"] = 1000
            };
            if (!string.IsNullOrWhiteSpace(skipToken)) options["$skipToken"] = skipToken;
            var body = JsonSerializer.Serialize(new { subscriptions, query, options });

            await requestLimiter.WaitAsync();
            string response;
            try
            {
                response = await HttpHelper.SendWithRetryAsync(
                    "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01",
                    token, null, telemetryPrefix,
                    method: HttpMethod.Post,
                    jsonBody: body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new { status = 0, error = Truncate(ex.Message, 300) };
            }
            finally
            {
                requestLimiter.Release();
            }

            var status = ParseStatus(response);
            if (status != 200)
                return new { status, error = Truncate(ResponseBody(response), 600) };

            try
            {
                using var doc = JsonDocument.Parse(ResponseBody(response));
                if (!doc.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    return new { status = 0, error = "Resource Graph response did not contain an objectArray data set." };

                rows.AddRange(data.EnumerateArray().Select(row => row.Clone()));
                if (rows.Count > maxRows)
                    return new { status = 206, error = $"Resource Graph evidence exceeded the {maxRows}-row safety limit." };

                skipToken = doc.RootElement.TryGetProperty("$skipToken", out var tokenElement)
                    ? tokenElement.GetString()
                    : doc.RootElement.TryGetProperty("skipToken", out tokenElement)
                        ? tokenElement.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(skipToken))
                {
                    var serializedRows = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(rows));
                    return new { status = 200, data = serializedRows };
                }
            }
            catch (JsonException ex)
            {
                return new { status = 0, parseError = ex.Message };
            }
        }

        return new { status = 206, error = "Resource Graph evidence exceeded the 10-page safety limit." };
    }

    private static BudgetEvidence CompactBudget(
        SubscriptionScope scope,
        string response,
        DateOnly periodStart,
        DateOnly periodEndInclusive)
    {
        var status = ParseStatus(response);
        if (status != 200)
            return new BudgetEvidence(scope.Id, scope.Name, status, 0, 0, null, null, 0, 0, Truncate(ResponseBody(response), 300));

        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            var values = doc.RootElement.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToArray()
                : [];
            double amount = 0;
            var actualNotifications = 0;
            var forecastNotifications = 0;
            foreach (var item in values)
            {
                if (!item.TryGetProperty("properties", out var props)) continue;
                if (props.TryGetProperty("amount", out var amountEl) && amountEl.TryGetDouble(out var a)) amount += a;
                if (props.TryGetProperty("notifications", out var notifications)
                    && notifications.ValueKind == JsonValueKind.Object)
                {
                    foreach (var notification in notifications.EnumerateObject())
                    {
                        var n = notification.Value;
                        if (n.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
                        var thresholdType = n.TryGetProperty("thresholdType", out var type) ? type.GetString() : null;
                        if (string.Equals(thresholdType, "Forecasted", StringComparison.OrdinalIgnoreCase)) forecastNotifications++;
                        else actualNotifications++;
                    }
                }
            }
            var spend = AzureQueryTools.ReadUnfilteredBudgetSpend(
                (scope.Id, scope.Name),
                response,
                periodStart,
                periodEndInclusive);
            return new BudgetEvidence(
                scope.Id,
                scope.Name,
                status,
                values.Length,
                Math.Round(amount, 2),
                spend.Success ? Math.Round(spend.Cost, 6) : null,
                spend.Success ? spend.Currency : null,
                actualNotifications,
                forecastNotifications,
                null);
        }
        catch (JsonException ex)
        {
            return new BudgetEvidence(scope.Id, scope.Name, status, 0, 0, null, null, 0, 0, ex.Message);
        }
    }

    private static CollectionEvidence CompactCollection(SubscriptionScope scope, string response)
    {
        var status = ParseStatus(response);
        if (status != 200)
            return new CollectionEvidence(scope.Id, scope.Name, status, 0, [], Truncate(ResponseBody(response), 300));

        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            var values = doc.RootElement.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToArray()
                : [];
            var names = values
                .Select(v => v.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Take(10)
                .Cast<string>()
                .ToArray();
            return new CollectionEvidence(scope.Id, scope.Name, status, values.Length, names, null);
        }
        catch (JsonException ex)
        {
            return new CollectionEvidence(scope.Id, scope.Name, status, 0, [], ex.Message);
        }
    }

    private static IReadOnlyList<MaturityScore> BuildScores(
        IReadOnlyList<SubscriptionScope> subscriptions,
        IReadOnlyList<BudgetEvidence> budgets,
        object taggingProjection,
        IReadOnlyList<CollectionEvidence> exports,
        IReadOnlyList<CollectionEvidence> scheduledActions,
        IReadOnlyList<CollectionEvidence> alerts,
        object policyProjection,
        object wasteProjection,
        object emptyGroupsProjection,
        double? mtdSpend,
        string? mtdCurrency,
        IReadOnlyDictionary<string, double> totalsByCurrency)
    {
        var tagRows = DataRows(taggingProjection);
        var totalResources = tagRows.Sum(r => IntProperty(r, "total"));
        var costCenter = tagRows.Sum(r => IntProperty(r, "costCenter"));
        var owner = tagRows.Sum(r => IntProperty(r, "owner"));
        var environment = tagRows.Sum(r => IntProperty(r, "environment"));
        var fullyTagged = tagRows.Sum(r => IntProperty(r, "fullyTagged"));
        var placeholderTags = tagRows.Sum(r => IntProperty(r, "placeholders"));
        var tagCoverage = totalResources == 0 ? 0 : fullyTagged * 100.0 / totalResources;
        var tagScore = totalResources == 0 ? 0 : tagCoverage switch
        {
            >= 95 => 5,
            >= 80 => 4,
            >= 60 => 3,
            >= 30 => 2,
            _ => 1
        };
        var tagSpread = string.Join(", ", subscriptions.Select(s =>
        {
            var row = tagRows.FirstOrDefault(r =>
                StringProperty(r, "subscriptionId").Equals(s.Id, StringComparison.OrdinalIgnoreCase));
            var total = row.ValueKind == JsonValueKind.Undefined ? 0 : IntProperty(row, "total");
            var governed = row.ValueKind == JsonValueKind.Undefined ? 0 : IntProperty(row, "fullyTagged");
            var pct = total == 0 ? 0 : Math.Round(governed * 100.0 / total, 1);
            return $"{pct}% in {s.Name}";
        }));

        var coveredBudgets = budgets.Count(b => b.Status == 200 && b.BudgetCount > 0);
        var allBudgetReadsOk = budgets.Count == subscriptions.Count && budgets.All(b => b.Status == 200);
        var allBudgetNotifications = budgets
            .Where(b => b.BudgetCount > 0)
            .All(b => b.EnabledActualNotifications > 0 && b.EnabledForecastNotifications > 0);
        var budgetScore = !allBudgetReadsOk ? 0
            : coveredBudgets == subscriptions.Count && allBudgetNotifications ? 5
            : coveredBudgets == subscriptions.Count ? 4
            : coveredBudgets > 0 ? 2
            : 1;
        var notificationCount = budgets.Sum(b => b.EnabledActualNotifications + b.EnabledForecastNotifications);

        var exportCount = exports.Sum(e => e.Count);
        var exportCoverage = exports.Count(e => e.Status == 200 && e.Count > 0);
        var exportsScore = exports.All(e => e.Status == 200)
            ? exportCoverage == subscriptions.Count ? 5 : exportCount > 0 ? 2 : 0
            : 0;

        var alertCount = alerts.Sum(a => a.Count);
        var actionCount = scheduledActions.Sum(a => a.Count);
        var alertCoverage = subscriptions.Count(s =>
            alerts.Any(a => a.SubscriptionId == s.Id && a.Count > 0)
            || scheduledActions.Any(a => a.SubscriptionId == s.Id && a.Count > 0));
        var alertsReadable = alerts.All(a => a.Status == 200) && scheduledActions.All(a => a.Status == 200);
        var alertsScore = !alertsReadable ? 0
            : alertCoverage == subscriptions.Count && alertCount + actionCount > 0 ? 5
            : alertCoverage > 0 ? 1
            : 0;

        var policyRows = DataRows(policyProjection);
        var totalPolicies = policyRows.Sum(r => IntProperty(r, "totalAssignments"));
        var finOpsPolicies = policyRows.Sum(r => IntProperty(r, "finOpsAssignments"));
        var policyCoverage = subscriptions.Count(s => policyRows.Any(r =>
            StringProperty(r, "subscriptionId").Equals(s.Id, StringComparison.OrdinalIgnoreCase)
            && IntProperty(r, "finOpsAssignments") > 0));
        var policyScore = ProjectionStatus(policyProjection) != 200 ? 0
            : policyCoverage == subscriptions.Count ? 5
            : policyCoverage * 2 >= subscriptions.Count ? 3
            : policyCoverage > 0 ? 2
            : 0;

        var wasteRows = DataRows(wasteProjection);
        var commonWaste = wasteRows.Sum(r => IntProperty(r, "wasteCount"));
        var emptyGroupRows = DataRows(emptyGroupsProjection);
        var emptyGroups = emptyGroupRows.Sum(r => IntProperty(r, "emptyGroupCount"));
        var totalWaste = commonWaste + emptyGroups;
        var wasteReadable = ProjectionStatus(wasteProjection) == 200 && ProjectionStatus(emptyGroupsProjection) == 200;
        var wasteScore = !wasteReadable ? 0 : totalWaste switch
        {
            0 => 5,
            1 => 4,
            <= 3 => 3,
            <= 10 => 2,
            _ => 1
        };
        var emptyGroupSpread = string.Join(", ", subscriptions.Select(s =>
        {
            var row = emptyGroupRows.FirstOrDefault(r =>
                StringProperty(r, "subscriptionId").Equals(s.Id, StringComparison.OrdinalIgnoreCase));
            var count = row.ValueKind == JsonValueKind.Undefined ? 0 : IntProperty(row, "emptyGroupCount");
            return $"{count} in {s.Name}";
        }));

        var visibleSubscriptions = budgets.Count(b => b.Status == 200 && b.CurrentSpend is not null);
        var visibilityScore = visibleSubscriptions == 0 ? 0
            : visibleSubscriptions < subscriptions.Count ? 1
            : tagCoverage >= 80 ? 4
            : tagCoverage >= 30 ? 3
            : 2;
        var spendSpread = string.Join(", ", budgets
            .Where(b => b.CurrentSpend is not null)
            .OrderByDescending(b => b.CurrentSpend)
            .Select(b => $"{b.CurrentSpend!.Value.ToString("N2", CultureInfo.InvariantCulture)} {b.Currency ?? "currency unknown"} in {b.SubscriptionName}"));
        var spendSummary = FormatCostSummary(mtdSpend, mtdCurrency, totalsByCurrency);

        return
        [
            new("budgets", "Budgets & thresholds", budgetScore,
                $"{coveredBudgets}/{subscriptions.Count} subscriptions have budgets; {budgets.Sum(b => b.BudgetCount)} budgets expose {notificationCount} enabled actual/forecast notifications and {spendSummary} from strict unfiltered monthly budgets."),
            new("tagging", "Tagging for accountability", tagScore,
                $"Valid CostCenter+Owner+Environment coverage is {Math.Round(tagCoverage, 1)}% across {totalResources} resources ({tagSpread}); exact valid-key counts are CostCenter={costCenter}, Owner={owner}, Environment={environment}, with {placeholderTags} placeholder values excluded."),
            new("exports", "Cost data exports", exportsScore,
                $"{exportCount} exports cover {exportCoverage}/{subscriptions.Count} subscriptions; {exports.Count(e => e.Status == 200)}/{subscriptions.Count} export-list calls succeeded."),
            new("alerts", "Cost alerts & scheduled actions", alertsScore,
                $"{alertCount} cost alerts and {actionCount} scheduled actions cover {alertCoverage}/{subscriptions.Count} subscriptions; {alerts.Count(a => a.Status == 200) + scheduledActions.Count(a => a.Status == 200)}/{subscriptions.Count * 2} list calls succeeded."),
            new("policy", "Governance guardrails", policyScore,
                $"{finOpsPolicies} FinOps-related policy assignments were found among {totalPolicies} total assignments, covering {policyCoverage}/{subscriptions.Count} subscriptions."),
            new("waste", "Waste identification & cleanup", wasteScore,
                $"{totalWaste} waste items were found: {commonWaste} unattached disks/orphaned IPs/empty paid App Service plans plus {emptyGroups} empty resource groups ({emptyGroupSpread})."),
            new("visibility", "Cost visibility & ownership", visibilityScore,
                $"Budget currentSpend provides {spendSummary} across {visibleSubscriptions}/{subscriptions.Count} subscriptions ({spendSpread}); governed ownership-tag coverage is {Math.Round(tagCoverage, 1)}% across {totalResources} resources.")
        ];
    }

    private static string FormatCostSummary(
        double? total,
        string? currency,
        IReadOnlyDictionary<string, double> totalsByCurrency)
    {
        if (total is not null && !string.IsNullOrWhiteSpace(currency))
            return $"{total.Value.ToString("N2", CultureInfo.InvariantCulture)} {currency} MTD";
        if (totalsByCurrency.Count > 0)
            return string.Join(" + ", totalsByCurrency
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => $"{kvp.Value.ToString("N2", CultureInfo.InvariantCulture)} {kvp.Key} MTD"));
        return "no validated MTD spend";
    }

    private static List<JsonElement> DataRows(object projection)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(projection));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];
            return data.EnumerateArray().Select(r => r.Clone()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int ProjectionStatus(object projection)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(projection));
            return doc.RootElement.TryGetProperty("status", out var status) && status.TryGetInt32(out var value)
                ? value
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static int IntProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static string StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
            ? value.GetString() ?? ""
            : "";

    private static IEnumerable<string> StringArrayProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static (List<SubscriptionScope> Subscriptions, string? Error) ParseSubscriptions(string json)
    {
        var scopes = new List<SubscriptionScope>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (scopes, "subscriptionsJson must be a JSON array.");
            if (doc.RootElement.GetArrayLength() > 500)
                return (scopes, "subscriptionsJson supports at most 500 entries; split larger estates into explicit scopes.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var rawId = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                rawId = rawId?.Trim();
                if (rawId?.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) == true)
                    rawId = rawId["/subscriptions/".Length..].Trim('/');
                if (!Guid.TryParse(rawId, out var id)) continue;
                var name = item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString()
                    : null;
                var canonicalId = id.ToString();
                if (!seen.Add(canonicalId)) continue;
                scopes.Add(new SubscriptionScope(canonicalId, string.IsNullOrWhiteSpace(name) ? canonicalId : name!));
            }
            return (scopes, null);
        }
        catch (JsonException ex)
        {
            return (scopes, $"Invalid subscriptionsJson: {ex.Message}");
        }
    }

    private static int ParseStatus(string response)
    {
        var line = response.Split('\n', 2)[0];
        var parts = line.Split(' ', 3);
        return parts.Length > 1 && int.TryParse(parts[1], out var status) ? status : 0;
    }

    private static string ResponseBody(string response)
    {
        var newline = response.IndexOf('\n');
        return newline >= 0 ? response[(newline + 1)..] : "";
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private sealed record SubscriptionScope(string Id, string Name);

    private sealed record BudgetEvidence(
        string SubscriptionId,
        string SubscriptionName,
        int Status,
        int BudgetCount,
        double ConfiguredBudgetAmountTotal,
        double? CurrentSpend,
        string? Currency,
        int EnabledActualNotifications,
        int EnabledForecastNotifications,
        string? Error);

    private sealed record CollectionEvidence(
        string SubscriptionId,
        string SubscriptionName,
        int Status,
        int Count,
        string[] Names,
        string? Error);

    private sealed record MaturityScore(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("score")] int Score,
        [property: JsonPropertyName("detail")] string Detail);
}
