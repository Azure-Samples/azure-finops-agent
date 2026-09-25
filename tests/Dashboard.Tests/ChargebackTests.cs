using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class ChargebackTests
{
    private const string Sub = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public void SchemaRequiresOnlyTheSubscriptionScope()
    {
        var tool = new ChargebackTools(new UserTokens { UserId = 7 }).Create().First(t => t.Name == "GetChargebackReport");
        Assert.Equal("GetChargebackReport", tool.Name);
        var required = tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["subscriptionsJson"], required);
        Assert.Contains("instead of QueryAzure", tool.Description);
    }

    [Fact]
    public async Task InvalidInputFailsBeforeAnyNetworkRequest()
    {
        var tool = new ChargebackTools(new UserTokens { UserId = 7 }).Create().First(t => t.Name == "GetChargebackReport");
        foreach (var arguments in new[]
        {
            new AIFunctionArguments { ["subscriptionsJson"] = "[]" },
            new AIFunctionArguments { ["subscriptionsJson"] = $"[\"{Sub}\"]", ["topServices"] = "0" },
            new AIFunctionArguments { ["subscriptionsJson"] = $"[\"{Sub}\"]", ["month"] = "2026/09" },
            new AIFunctionArguments { ["subscriptionsJson"] = $"[\"{Sub}\"]", ["tagKeys"] = "a,b,c,d,e,f" }
        })
        {
            var response = (await tool.InvokeAsync(arguments))!.ToString()!;
            Assert.StartsWith("HTTP 400", response);
            Assert.Contains("No request was sent", response);
        }
    }

    [Fact]
    public async Task NumericArgumentForAStringParameterIsCoercedInsteadOfFailingTheCall()
    {
        var inner = new ChargebackTools(new UserTokens { UserId = 7 }).Create().First(t => t.Name == "GetChargebackReport");
        var tool = new ProtectedTool(inner);
        using var arguments = JsonDocument.Parse($$"""{"subscriptionsJson":"[\"{{Sub}}\"]","topServices":0}""");
        var args = new AIFunctionArguments(arguments.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));
        var response = (await tool.InvokeAsync(args))!.ToString()!;
        Assert.Contains("topServices must be an integer", response);
        Assert.Equal("0", args["topServices"]);
    }

    [Fact]
    public async Task MissingArmTokenRequestsSignInWithDefaults()
    {
        var tool = new ChargebackTools(new UserTokens { UserId = 7 }).Create().First(t => t.Name == "GetChargebackReport");
        var response = (await tool.InvokeAsync(new AIFunctionArguments { ["subscriptionsJson"] = $"[{{\"id\":\"{Sub}\",\"name\":\"Prod\"}}]" }))!.ToString()!;
        Assert.StartsWith("HTTP 401", response);
    }

    [Fact]
    public void CurrentMonthComparesTheSameDayWindowAndClampsShortMonths()
    {
        var (sep, error) = ChargebackTools.ResolvePeriods("", new DateOnly(2026, 9, 25));
        Assert.Null(error);
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 25)), (sep.Current.From, sep.Current.ToInclusive));
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 25)), (sep.Comparison.From, sep.Comparison.ToInclusive));

        var (march, _) = ChargebackTools.ResolvePeriods("", new DateOnly(2026, 3, 31));
        Assert.Equal(new DateOnly(2026, 2, 28), march.Comparison.ToInclusive);

        var (past, _) = ChargebackTools.ResolvePeriods("2026-07", new DateOnly(2026, 9, 25));
        Assert.Equal((new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)), (past.Current.From, past.Current.ToInclusive));
        Assert.Equal((new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)), (past.Comparison.From, past.Comparison.ToInclusive));

        Assert.NotNull(ChargebackTools.ResolvePeriods("2026-10", new DateOnly(2026, 9, 25)).Error);
    }

    [Fact]
    public void AllocationTagPrefersTheMostCarriedCandidateAndReportsSeparatorVariants()
    {
        var allocation = ChargebackTools.SelectAllocationKey(["CostCenter", "Owner"],
            [("owner", 9), ("Owner", 3), ("cost-center", 4), ("CostCenter", 2)])!;
        Assert.Equal("Owner", allocation.Tag);
        Assert.Equal("owner", allocation.Key);
        Assert.Equal(12, allocation.TaggedResources);
        Assert.Empty(allocation.OtherVariants);

        var costCenter = ChargebackTools.SelectAllocationKey(["CostCenter"], [("cost-center", 4), ("CostCenter", 2)])!;
        Assert.Equal("cost-center", costCenter.Key);
        Assert.Equal(["CostCenter"], costCenter.OtherVariants);

        var absent = ChargebackTools.SelectAllocationKey(["CostCenter", "Owner"], [])!;
        Assert.Equal("CostCenter", absent.Key);
    }

    [Fact]
    public void QueryUsesTwoGroupingsAndAnInclusiveEnd()
    {
        using var doc = JsonDocument.Parse(ChargebackTools.BuildQueryBody("Owner",
            new("current", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 25))));
        Assert.Equal("ActualCost", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("2026-09-25", doc.RootElement.GetProperty("timePeriod").GetProperty("to").GetString());
        var grouping = doc.RootElement.GetProperty("dataset").GetProperty("grouping");
        Assert.Equal(2, grouping.GetArrayLength());
        Assert.Equal("TagKey", grouping[0].GetProperty("type").GetString());
        Assert.Equal("Owner", grouping[0].GetProperty("name").GetString());
        Assert.Equal("ServiceName", grouping[1].GetProperty("name").GetString());
    }

    [Fact]
    public void SummaryComputesTeamTotalsTopServicesAndChangeDeterministically()
    {
        const string current = """{"properties":{"nextLink":null,"columns":[{"name":"Cost"},{"name":"TagKey"},{"name":"TagValue"},{"name":"ServiceName"},{"name":"Currency"}],"rows":[[100.5,"owner","alice","Storage","USD"],[50,"owner","Alice","Virtual Machines","USD"],[25.25,"owner","bob","Storage","USD"],[10,"","","Bandwidth","USD"]]}}""";
        const string comparison = """{"properties":{"columns":[{"name":"Cost"},{"name":"TagKey"},{"name":"TagValue"},{"name":"ServiceName"},{"name":"Currency"}],"rows":[[75.25,"owner","alice","Storage","USD"],[20,"","","Bandwidth","USD"]]}}""";
        var currentRows = ChargebackTools.ParseCostRows(current);
        var comparisonRows = ChargebackTools.ParseCostRows(comparison);
        Assert.Null(currentRows.Error);
        var (periods, _) = ChargebackTools.ResolvePeriods("", new DateOnly(2026, 9, 25));
        var allocation = ChargebackTools.SelectAllocationKey(["Owner"], [("Owner", 3)])!;

        using var doc = JsonDocument.Parse(ChargebackTools.Summarize([(Sub, "Prod")], allocation, periods,
            [new(Sub, "Prod", "current", 200, 1, false, null, currentRows.Rows), new(Sub, "Prod", "comparison", 200, 1, false, null, comparisonRows.Rows)],
            [], throttled: false, top: 1, discoveryTruncated: false, retrievedAtUtc: "t"));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("complete").GetBoolean());
        var totals = root.GetProperty("totalsByCurrency").GetProperty("USD");
        Assert.Equal(185.75m, totals.GetProperty("current").GetDecimal());
        Assert.Equal(95.25m, totals.GetProperty("comparison").GetDecimal());
        Assert.Equal(90.50m, totals.GetProperty("difference").GetDecimal());
        Assert.Equal(95.0m, totals.GetProperty("percentChange").GetDecimal());
        Assert.Equal(10m, totals.GetProperty("untaggedCurrent").GetDecimal());

        var teams = root.GetProperty("teams").EnumerateArray().ToList();
        Assert.Equal(["alice", "bob", ChargebackTools.UntaggedLabel], teams.Select(t => t.GetProperty("team").GetString()));
        var alice = teams[0];
        Assert.Equal(150.5m, alice.GetProperty("current").GetDecimal());
        Assert.Equal(100.0m, alice.GetProperty("percentChange").GetDecimal());
        Assert.Equal("Storage", alice.GetProperty("topServices")[0].GetProperty("service").GetString());
        Assert.Equal(1, alice.GetProperty("otherServices").GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, teams[1].GetProperty("percentChange").ValueKind);
        Assert.True(teams[2].GetProperty("untagged").GetBoolean());
        Assert.True(ProtectedTool.InspectEvidence(root.GetRawText()).Success);
    }

    [Fact]
    public void SubscriptionComparisonRanksSubscriptionsWithHostComputedChange()
    {
        const string Other = "00000000-0000-0000-0000-000000000002";
        const string Columns = "\"columns\":[{\"name\":\"Cost\"},{\"name\":\"ServiceName\"},{\"name\":\"Currency\"}]";
        List<ChargebackTools.CostRow> Rows(string rows)
        {
            var parsed = ChargebackTools.ParseCostRows("{\"properties\":{" + Columns + ",\"rows\":" + rows + "}}");
            Assert.Null(parsed.Error);
            return parsed.Rows;
        }
        var (periods, _) = ChargebackTools.ResolvePeriods("", new DateOnly(2026, 9, 25));
        using var doc = JsonDocument.Parse(ChargebackTools.SummarizeSubscriptions([(Sub, "Dev"), (Other, "Prod")], periods,
            [
                new(Sub, "Dev", "current", 200, 1, false, null, Rows("""[[40,"Storage","USD"],[10,"Bandwidth","USD"]]""")),
                new(Sub, "Dev", "comparison", 200, 1, false, null, Rows("""[[25,"Storage","USD"]]""")),
                new(Other, "Prod", "current", 200, 1, false, null, Rows("""[[150,"Virtual Machines","USD"]]""")),
                new(Other, "Prod", "comparison", 200, 1, false, null, Rows("[]"))
            ],
            [], throttled: false, top: 1, retrievedAtUtc: "t"));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Equal("ActualCost", root.GetProperty("costType").GetString());
        var rows = root.GetProperty("subscriptions").EnumerateArray().ToList();
        Assert.Equal(["Prod", "Dev"], rows.Select(r => r.GetProperty("subscriptionName").GetString()));
        Assert.Equal(1, rows[0].GetProperty("rank").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("percentChange").ValueKind);
        Assert.Equal(75.0m, rows[0].GetProperty("sharePercent").GetDecimal());
        Assert.Equal(50m, rows[1].GetProperty("current").GetDecimal());
        Assert.Equal(25m, rows[1].GetProperty("difference").GetDecimal());
        Assert.Equal(100.0m, rows[1].GetProperty("percentChange").GetDecimal());
        Assert.Equal(1, rows[1].GetProperty("otherServices").GetProperty("count").GetInt32());
        var totals = root.GetProperty("totalsByCurrency").GetProperty("USD");
        Assert.Equal(200m, totals.GetProperty("current").GetDecimal());
        Assert.Equal(700.0m, totals.GetProperty("percentChange").GetDecimal());
        Assert.True(ProtectedTool.InspectEvidence(root.GetRawText()).Success);
        var table = root.GetProperty("answerTable").GetString()!;
        Assert.Contains("| 1 | Prod | USD 150.00 | USD 0.00 | +USD 150.00 | n/a (no comparison spend) |", table);
        Assert.Contains("| 2 | Dev | USD 50.00 | USD 25.00 | +USD 25.00 | +100.0% |", table);
        Assert.Equal("Prod is the highest of 2 subscriptions at USD 150.00 ActualCost for 2026-09-01 to 2026-09-25, with no ActualCost in the comparison window (vs 2026-08-01 to 2026-08-25).",
            root.GetProperty("headline").GetString());

        using var body = JsonDocument.Parse(ChargebackTools.BuildServiceQueryBody(periods.Comparison));
        Assert.Equal("ActualCost", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("2026-08-25", body.RootElement.GetProperty("timePeriod").GetProperty("to").GetString());
        Assert.Equal("ServiceName", body.RootElement.GetProperty("dataset").GetProperty("grouping")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void ThrottledSubscriptionComparisonIsPartialAndFailsEvidenceInspection()
    {
        var (periods, _) = ChargebackTools.ResolvePeriods("", new DateOnly(2026, 9, 25));
        using var doc = JsonDocument.Parse(ChargebackTools.SummarizeSubscriptions([(Sub, "Dev")], periods,
            [new(Sub, "Dev", "current", 429, 1, false, "HTTP 429 TooManyRequests", []), new(Sub, "Dev", "comparison", 0, 0, false, "not attempted after tenant throttle", [])],
            [], throttled: true, top: 3, retrievedAtUtc: "t"));
        Assert.False(doc.RootElement.GetProperty("complete").GetBoolean());
        Assert.False(ProtectedTool.InspectEvidence(doc.RootElement.GetRawText()).Success);
    }

    [Fact]
    public void ThrottledReportIsPartialAndFailsEvidenceInspection()
    {
        var (periods, _) = ChargebackTools.ResolvePeriods("", new DateOnly(2026, 9, 25));
        var allocation = ChargebackTools.SelectAllocationKey(["Owner"], [("Owner", 3)])!;
        using var doc = JsonDocument.Parse(ChargebackTools.Summarize([(Sub, "Prod")], allocation, periods,
            [new(Sub, "Prod", "current", 429, 1, false, "HTTP 429 TooManyRequests", []), new(Sub, "Prod", "comparison", 0, 0, false, "not attempted after tenant throttle", [])],
            [JsonSerializer.SerializeToElement(new { retryAtUtc = "2026-09-25T13:00:00Z" })], throttled: true, top: 3, discoveryTruncated: false, retrievedAtUtc: "t"));
        Assert.False(doc.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("2026-09-25T13:00:00Z", doc.RootElement.GetProperty("retryAtUtc").GetString());
        Assert.False(ProtectedTool.InspectEvidence(doc.RootElement.GetRawText()).Success);
    }
}
