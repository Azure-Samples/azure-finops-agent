using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.VisualBasic.FileIO;

namespace AzureFinOps.Dashboard.AI.Tools;

public sealed class CopilotUsageTools(UserTokens tokens)
{
    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GetCopilotUsage, "GetCopilotUsage",
            "Read Microsoft 365 Copilot licensed-user activity without returning a huge raw report. " +
            "Use for inactive-user lists and activity counts before calculating license savings. The host reads the supported v1.0 CSV report, " +
            "classifies activity against its Report Refresh Date, counts all rows, then filters and pages the results. " +
            "Requires delegated Graph Reports.Read.All and a supported directory role; do not repeatedly reconnect ARM for a reports permission error. " +
            "This is periodically refreshed licensed-user activity, not current assignment inventory, unlicensed Copilot Chat usage, or proof of financial ROI. " +
            "Never subtract current subscribedSkus assignments from a differently dated active-user report. Preserve unknown activity and anonymized identities. " +
            "Follow nextOffset only for requested detail; each call reads the report again, so combine pages only when refresh dates and totals agree. " +
            "When the tenant has no usage reports (reportAvailable=false), the host reads current subscribedSkus: zero assigned Copilot seats yields determinate zero counts and zero waste; otherwise counts stay unknown.");
    }

    private async Task<string> GetCopilotUsage(
        [Description("Supported v1 report period: D7, D30, D90 or D180. Default D30. Do not silently replace a requested custom date range.")] string period = "D30",
        [Description("Host-side activity selection: inactive (default), active, unknown or all. Inactive means no activity reported in the report period, not proven license waste.")] string activity = "inactive",
        [Description("Detail rows per page, 0-200; default 50. Use 0 for counts only. Totals always cover the full report before paging.")] string limit = "50",
        [Description("Zero-based offset within the selected activity cohort. Follow the returned nextOffset; default 0.")] string offset = "0")
    {
        using var span = HttpHelper.Telemetry.StartActivity("GetCopilotUsage");
        var days = period switch { "D7" => 7, "D30" => 30, "D90" => 90, "D180" => 180, _ => 0 };
        if (days == 0 || activity is not ("inactive" or "active" or "unknown" or "all")
            || !int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize) || pageSize is < 0 or > 200
            || !int.TryParse(offset, NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start < 0)
        {
            span?.SetStatus(ActivityStatusCode.Error, "Invalid report selection");
            return "HTTP 400 BadRequest\nUse period D7/D30/D90/D180, activity inactive/active/unknown/all, limit 0-200 and a non-negative offset. No request was sent.";
        }
        var token = tokens.GraphToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("GraphToken", span, "graph", graphTier: "licenses");

        var response = await HttpHelper.SendWithRetryAsync(
            $"https://graph.microsoft.com/v1.0/copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='{period}')",
            token, span, "graph.copilot_usage", maxResponseChars: 8_000_000);
        if (IsTenantWithoutUsageReports(response))
        {
            var inventory = await HttpHelper.SendWithRetryAsync(
                "https://graph.microsoft.com/v1.0/subscribedSkus?$select=skuPartNumber,consumedUnits,servicePlans",
                token, span, "graph.subscribed_skus", maxResponseChars: 2_000_000);
            return ReportUnavailable(days, activity, inventory.StartsWith("HTTP 200 ", StringComparison.Ordinal)
                ? CountAssignedCopilotSeats(inventory[(inventory.IndexOf('\n') + 1)..])
                : null);
        }
        if (!response.StartsWith("HTTP 200 ", StringComparison.Ordinal)) return response;
        var result = SummarizeReport(response[(response.IndexOf('\n') + 1)..], days, activity, pageSize, start);
        if (result.StartsWith("HTTP 502 ", StringComparison.Ordinal))
            span?.SetStatus(ActivityStatusCode.Error, "Invalid or incomplete Copilot usage report");
        return result;
    }

    internal static string SummarizeReport(string csv, int days, string activity, int limit, int offset)
    {
        if (csv.Contains("[PARTIAL RESULT:", StringComparison.Ordinal))
            return InvalidReport("The report exceeded the bounded read limit. No complete counts or inactive-user claims are available.");

        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        try
        {
            var headers = parser.ReadFields();
            if (headers is null || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
                return InvalidReport("The CSV header is missing or ambiguous.");
            int Column(string name) => Array.FindIndex(headers, header => header.Equals(name, StringComparison.OrdinalIgnoreCase));
            var refreshColumn = Column("Report Refresh Date");
            var identityColumn = Column("User Principal Name");
            var activityColumn = Column("Last Activity Date");
            var displayColumn = Column("Display Name");
            var periodColumn = Column("Report Period");
            if (refreshColumn < 0 || identityColumn < 0 || activityColumn < 0 || periodColumn < 0)
                return InvalidReport("The response does not contain the required licensed-user CSV columns.");

            var rows = new List<object>();
            var refreshDates = new SortedSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pageCharacters = 0;
            var pageFull = false;
            int total = 0, active = 0, inactive = 0, unknown = 0, matches = 0;
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                if (fields is null) continue;
                if (fields.Length != headers.Length)
                    return InvalidReport("A CSV row does not match the report header.");
                if (!int.TryParse(fields[periodColumn], NumberStyles.None, CultureInfo.InvariantCulture, out var rowPeriod) || rowPeriod != days)
                    return InvalidReport("The returned period differs from the requested period.");
                if (string.IsNullOrWhiteSpace(fields[identityColumn]) || !identities.Add(fields[identityColumn]))
                    return InvalidReport("A licensed-user identifier is missing or duplicated; unique-user counts cannot be verified.");
                total++;
                var refreshValid = TryDate(fields[refreshColumn], out var refreshDate);
                if (refreshValid) refreshDates.Add(refreshDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var lastActivity = fields[activityColumn];
                var classification = !refreshValid ? "unknown"
                    : string.IsNullOrWhiteSpace(lastActivity) ? "inactive"
                    : !TryDate(lastActivity, out var observed) || observed > refreshDate ? "unknown"
                    : refreshDate.DayNumber - observed.DayNumber < days ? "active" : "inactive";
                if (classification == "active") active++;
                else if (classification == "inactive") inactive++;
                else unknown++;
                if (activity != "all" && activity != classification) continue;
                if (matches >= offset && rows.Count < limit && !pageFull)
                {
                    var row = new
                    {
                        userPrincipalName = fields[identityColumn],
                        displayName = displayColumn >= 0 ? fields[displayColumn] : null,
                        lastActivityDate = string.IsNullOrWhiteSpace(lastActivity) ? null : lastActivity,
                        activity = classification,
                        reportRefreshDate = refreshValid ? fields[refreshColumn] : null
                    };
                    var rowLength = JsonSerializer.Serialize(row).Length;
                    if (rowLength > 6_000)
                        return InvalidReport("A report identity exceeds the bounded detail limit; no complete user list is available.");
                    if (pageCharacters + rowLength > 6_000)
                        pageFull = true;
                    else
                    {
                        rows.Add(row);
                        pageCharacters += rowLength;
                    }
                }
                matches++;
            }
            if (refreshDates.Count > 1)
                return InvalidReport("Rows contain different report refresh dates; cohort counts cannot be safely combined.");

            return JsonSerializer.Serialize(new
            {
                source = "Microsoft Graph Copilot licensed-user usage report",
                period = "D" + days.ToString(CultureInfo.InvariantCulture),
                sourceEvidence = new { freshness = "periodic", reportRefreshDate = refreshDates.SingleOrDefault(), retrievedAtUtc = DateTimeOffset.UtcNow },
                licensedUsersOnly = true,
                identitiesMayBeAnonymized = true,
                totalReportedUsers = total,
                activeUsers = active,
                inactiveUsers = inactive,
                unknownActivityUsers = unknown,
                activity,
                matchedUsers = matches,
                totalsComplete = true,
                offset,
                returnedUsers = rows.Count,
                complete = offset >= matches || rows.Count >= matches - offset,
                nextOffset = limit > 0 && offset < matches && rows.Count < matches - offset ? offset + rows.Count : (int?)null,
                users = rows,
                interpretation = "Inactive means no activity reported within this report period. It does not prove a license can be removed or establish savings. Do not reconcile current assignments with this cohort without matching identities and dates."
            });
        }
        catch (MalformedLineException)
        {
            return InvalidReport("The provider returned malformed CSV; no complete counts are available.");
        }
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    // The Microsoft 365 reporting service answers 404 UnknownTenantId for a directory it has never
    // provisioned (no Microsoft 365 workloads). That is a definitive "no report exists" outcome, not a
    // transient or permission fault, so it is returned as structured unavailability with unknown counts.
    internal static bool IsTenantWithoutUsageReports(string response) =>
        response.StartsWith("HTTP 404 ", StringComparison.Ordinal)
        && response.Contains("UnknownTenantId", StringComparison.Ordinal);

    // Returns the assigned Microsoft 365 Copilot seat count from subscribedSkus, or null when the
    // inventory cannot be read reliably. A SKU counts as Copilot when its part number or any of its
    // service plans names Copilot, so bundles that carry a Copilot plan are never mistaken for zero seats.
    internal static int? CountAssignedCopilotSeats(string subscribedSkusJson)
    {
        try
        {
            using var document = JsonDocument.Parse(subscribedSkusJson);
            if (!document.RootElement.TryGetProperty("value", out var skus) || skus.ValueKind != JsonValueKind.Array) return null;
            var seats = 0;
            foreach (var sku in skus.EnumerateArray())
            {
                var copilot = sku.TryGetProperty("skuPartNumber", out var part) && part.ValueKind == JsonValueKind.String
                    && part.GetString()!.Contains("Copilot", StringComparison.OrdinalIgnoreCase);
                if (!copilot && sku.TryGetProperty("servicePlans", out var plans) && plans.ValueKind == JsonValueKind.Array)
                    copilot = plans.EnumerateArray().Any(plan => plan.TryGetProperty("servicePlanName", out var name)
                        && name.ValueKind == JsonValueKind.String && name.GetString()!.Contains("Copilot", StringComparison.OrdinalIgnoreCase));
                if (!copilot) continue;
                if (!sku.TryGetProperty("consumedUnits", out var consumed) || !consumed.TryGetInt32(out var units) || units < 0) return null;
                seats += units;
            }
            return seats;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string ReportUnavailable(int days, string activity, int? assignedCopilotSeats)
    {
        var noSeats = assignedCopilotSeats == 0;
        return JsonSerializer.Serialize(new
        {
            source = "Microsoft Graph Copilot licensed-user usage report",
            period = "D" + days.ToString(CultureInfo.InvariantCulture),
            reportAvailable = false,
            reason = noSeats
                ? "The Microsoft 365 reporting service does not recognize this tenant (UnknownTenantId), so it has no Copilot activity report. None is needed: current inventory shows zero assigned Copilot seats, so there are no licensed users whose activity could be measured."
                : "The Microsoft 365 reporting service does not recognize this tenant (UnknownTenantId), so no licensed-user Copilot activity report exists for it.",
            activityReportRequired = !noSeats,
            sourceEvidence = noSeats
                ? (object)new { freshness = "fresh", basis = "current subscribedSkus assignments", retrievedAtUtc = DateTimeOffset.UtcNow }
                : new { freshness = "unavailable", retrievedAtUtc = DateTimeOffset.UtcNow },
            licenseInventory = new
            {
                source = "Microsoft Graph subscribedSkus (current assignments)",
                read = assignedCopilotSeats is not null,
                assignedCopilotSeats
            },
            licensedUsersOnly = true,
            totalReportedUsers = noSeats ? 0 : (int?)null,
            activeUsers = noSeats ? 0 : (int?)null,
            inactiveUsers = noSeats ? 0 : (int?)null,
            unknownActivityUsers = noSeats ? 0 : (int?)null,
            activity,
            totalsComplete = noSeats,
            users = Array.Empty<object>(),
            inactiveLicenseMonthlyWaste = noSeats ? 0 : (int?)null,
            interpretation = noSeats
                ? "Determinate answer, not unknown: 0 Copilot seats assigned, 0 seats actively used, 0 inactive licensed users (none to list) and zero monthly inactive-license waste in any currency, because an empty licensed-user population has nothing to measure or price. State these as zeros; the only uncovered area is unlicensed Copilot Chat activity, which is not license waste."
                : "Activity counts are unknown, not zero, and inactive-license waste cannot be calculated until the tenant produces usage reports. Report assigned Copilot seats from subscribedSkus consumedUnits."
        });
    }

    private static string InvalidReport(string detail) => "HTTP 502 BadGateway\n" + detail;
}
