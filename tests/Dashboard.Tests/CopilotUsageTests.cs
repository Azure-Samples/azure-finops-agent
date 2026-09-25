using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class CopilotUsageTests
{
    private const string Header = "Report Refresh Date,User Principal Name,Display Name,Last Activity Date,Report Period\n";

    [Fact]
    public void UnprovisionedReportingTenantIsStructuredUnavailabilityWithUnknownCounts()
    {
        const string response = "HTTP 404 NotFound\n{\"error\":{\"code\":\"UnknownError\",\"message\":\"{\\u0022error\\u0022:{\\u0022code\\u0022:\\u0022UnknownTenantId\\u0022}}\"}}";
        Assert.True(CopilotUsageTools.IsTenantWithoutUsageReports(response));
        Assert.False(CopilotUsageTools.IsTenantWithoutUsageReports("HTTP 404 NotFound\n{\"error\":{\"code\":\"ResourceNotFound\"}}"));
        Assert.False(CopilotUsageTools.IsTenantWithoutUsageReports("HTTP 403 Forbidden\nUnknownTenantId"));

        using var document = JsonDocument.Parse(CopilotUsageTools.ReportUnavailable(30, "inactive", new(1, 3, 5)));
        var body = document.RootElement;
        Assert.False(body.GetProperty("reportAvailable").GetBoolean());
        Assert.False(body.GetProperty("totalsComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("totalReportedUsers").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("inactiveUsers").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("inactiveLicenseMonthlyWaste").ValueKind);
        Assert.Equal(0, body.GetProperty("users").GetArrayLength());

        using var unread = JsonDocument.Parse(CopilotUsageTools.ReportUnavailable(30, "inactive", null));
        Assert.False(unread.RootElement.GetProperty("totalsComplete").GetBoolean());
        Assert.False(unread.RootElement.GetProperty("licenseInventory").GetProperty("read").GetBoolean());
    }

    [Fact]
    public void UnprovisionedReportingTenantWithoutCopilotSeatsHasDeterminateZeros()
    {
        const string skus = "{\"value\":[{\"skuPartNumber\":\"Microsoft_365_E5_(no_Teams)\",\"consumedUnits\":2,\"prepaidUnits\":{\"enabled\":50},\"servicePlans\":[{\"servicePlanName\":\"EXCHANGE_S_ENTERPRISE\"}]}," +
            "{\"skuPartNumber\":\"Microsoft_365_Copilot\",\"consumedUnits\":0,\"prepaidUnits\":{\"enabled\":0,\"warning\":0},\"servicePlans\":[]}]}";
        Assert.Equal(new CopilotUsageTools.CopilotInventory(1, 0, 0), CopilotUsageTools.ReadCopilotInventory(skus));
        Assert.Equal(new CopilotUsageTools.CopilotInventory(0, 0, 0), CopilotUsageTools.ReadCopilotInventory(
            "{\"value\":[{\"skuPartNumber\":\"Microsoft_365_E5_(no_Teams)\",\"consumedUnits\":2,\"prepaidUnits\":{\"enabled\":50},\"servicePlans\":[]}]}"));
        Assert.Equal(new CopilotUsageTools.CopilotInventory(1, 4, 7), CopilotUsageTools.ReadCopilotInventory(
            "{\"value\":[{\"skuPartNumber\":\"BUNDLE\",\"consumedUnits\":4,\"prepaidUnits\":{\"enabled\":5,\"warning\":2},\"servicePlans\":[{\"servicePlanName\":\"M365_COPILOT_APPS\"}]}]}"));
        Assert.Equal(new CopilotUsageTools.CopilotInventory(1, 0, null), CopilotUsageTools.ReadCopilotInventory(
            "{\"value\":[{\"skuPartNumber\":\"Microsoft_365_Copilot\",\"consumedUnits\":0}]}"));
        Assert.Null(CopilotUsageTools.ReadCopilotInventory("{\"value\":[{\"skuPartNumber\":\"Microsoft_365_Copilot\"}]}"));
        Assert.Null(CopilotUsageTools.ReadCopilotInventory("not json"));

        using var document = JsonDocument.Parse(CopilotUsageTools.ReportUnavailable(30, "inactive", new(1, 0, 0)));
        var body = document.RootElement;
        Assert.False(body.GetProperty("reportAvailable").GetBoolean());
        Assert.True(body.GetProperty("totalsComplete").GetBoolean());
        Assert.Equal(0, body.GetProperty("inactiveUsers").GetInt32());
        Assert.Equal(0, body.GetProperty("activeUsers").GetInt32());
        Assert.Equal(0, body.GetProperty("inactiveLicenseMonthlyWaste").GetInt32());
        Assert.Equal(0, body.GetProperty("licenseInventory").GetProperty("assignedCopilotSeats").GetInt32());
        Assert.Equal(0, body.GetProperty("licenseInventory").GetProperty("enabledCopilotSeats").GetInt32());
        Assert.Contains("0 assigned Copilot seats", body.GetProperty("reason").GetString());
        Assert.DoesNotContain("purchase", body.GetProperty("interpretation").GetString(), StringComparison.OrdinalIgnoreCase);
        var figures = body.GetProperty("answerFigures").EnumerateArray().Select(f => f.GetString()).ToArray();
        Assert.Contains("Enabled Copilot seats (current inventory, not invoice-verified): 0", figures);
        Assert.Contains("Assigned Copilot seats: 0", figures);
        Assert.Contains("Monthly waste from inactive assigned Copilot licenses: 0", figures);

        using var idle = JsonDocument.Parse(CopilotUsageTools.ReportUnavailable(30, "inactive", new(1, 0, 10)));
        Assert.Equal(10, idle.RootElement.GetProperty("licenseInventory").GetProperty("unassignedEnabledCopilotSeats").GetInt32());
        Assert.Contains("Enabled but unassigned Copilot seats: 10",
            idle.RootElement.GetProperty("answerFigures").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(0, idle.RootElement.GetProperty("inactiveUsers").GetInt32());
        Assert.Contains("contract price", idle.RootElement.GetProperty("interpretation").GetString());
    }

    [Fact]
    public void LargeReportIsCountedBeforeFilteringAndPaging()
    {
        var csv = Header + string.Join('\n', Enumerable.Range(0, 1200)
            .Select(index => $"2026-09-19,user-{index},Synthetic user,,30"));
        var result = CopilotUsageTools.SummarizeReport(csv, 30, "inactive", 20, 10);
        using var document = JsonDocument.Parse(result);
        var body = document.RootElement;
        Assert.Equal(1200, body.GetProperty("totalReportedUsers").GetInt32());
        Assert.Equal(1200, body.GetProperty("inactiveUsers").GetInt32());
        Assert.True(body.GetProperty("totalsComplete").GetBoolean());
        Assert.False(body.GetProperty("complete").GetBoolean());
        Assert.Equal(20, body.GetProperty("users").GetArrayLength());
        Assert.Equal("user-10", body.GetProperty("users")[0].GetProperty("userPrincipalName").GetString());
        Assert.Equal(30, body.GetProperty("nextOffset").GetInt32());
        Assert.True(result.Length < 8_000);
        Assert.Equal("periodic", body.GetProperty("sourceEvidence").GetProperty("freshness").GetString());
    }

    [Fact]
    public void ClassificationUsesReportDateAndPreservesUnknownActivity()
    {
        var csv = Header
            + "2026-09-19,active,\"Synthetic, quoted\",2026-08-21,30\n"
            + "2026-09-19,old,Synthetic,2026-08-20,30\n"
            + "2026-09-19,never,Synthetic,,30\n"
            + "2026-09-19,unknown,Synthetic,not-a-date,30\n"
            + "2026-09-19,future,Synthetic,2026-09-20,30\n";
        using var document = JsonDocument.Parse(CopilotUsageTools.SummarizeReport(csv, 30, "all", 50, 0));
        var body = document.RootElement;
        Assert.Equal(1, body.GetProperty("activeUsers").GetInt32());
        Assert.Equal(2, body.GetProperty("inactiveUsers").GetInt32());
        Assert.Equal(2, body.GetProperty("unknownActivityUsers").GetInt32());
        Assert.Equal("Synthetic, quoted", body.GetProperty("users")[0].GetProperty("displayName").GetString());
        Assert.True(body.GetProperty("complete").GetBoolean());
    }

    [Theory]
    [InlineData("2026-09-19,synthetic,Synthetic,,7\n")]
    [InlineData("2026-09-19,synthetic,Synthetic,,30\n2026-09-18,second,Synthetic,,30\n")]
    [InlineData("2026-09-19,synthetic,\"unterminated,,30\n")]
    [InlineData("2026-09-19,synthetic,Synthetic,,30\n2026-09-19,synthetic,Synthetic,,30\n")]
    [InlineData("[PARTIAL RESULT: showing first 1KB]\n")]
    public void IncompleteOrMismatchedReportsNeverYieldSuccessfulCounts(string rows) =>
        Assert.StartsWith("HTTP 502", CopilotUsageTools.SummarizeReport(Header + rows, 30, "inactive", 50, 0));

    [Fact]
    public void TotalsOnlyDoesNotReturnIdentities()
    {
        using var document = JsonDocument.Parse(CopilotUsageTools.SummarizeReport(
            Header + "2026-09-19,synthetic,Synthetic,,30\n", 30, "inactive", 0, 0));
        Assert.Equal(1, document.RootElement.GetProperty("matchedUsers").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("users").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task InvalidSelectionFailsBeforeAnyNetworkRequest()
    {
        var tool = new CopilotUsageTools(new UserTokens { UserId = 101 }).Create().Single();
        var response = await tool.InvokeAsync(new AIFunctionArguments { ["period"] = "D28" });
        Assert.Contains("No request was sent", response!.ToString());
    }

    [Fact]
    public void LongDisplayNamesStillProduceInlinePagesAndAnAdvancingCursor()
    {
        var csv = Header + string.Join('\n', Enumerable.Range(0, 200)
            .Select(index => $"2026-09-19,user-{index},{new string('x', 500)},,30"));
        var result = CopilotUsageTools.SummarizeReport(csv, 30, "inactive", 200, 0);
        using var document = JsonDocument.Parse(result);
        var body = document.RootElement;
        Assert.Equal(200, body.GetProperty("matchedUsers").GetInt32());
        var returned = body.GetProperty("returnedUsers").GetInt32();
        Assert.InRange(returned, 1, 20);
        Assert.Equal(returned, body.GetProperty("nextOffset").GetInt32());
        Assert.True(result.Length < 8_000);
    }

    [Fact]
    public async Task MissingReportConsentOffersOnlyTheRelevantTier()
    {
        var tool = new CopilotUsageTools(new UserTokens { UserId = 101 }).Create().Single();
        var response = (await tool.InvokeAsync(new AIFunctionArguments()))!.ToString()!;
        Assert.StartsWith("HTTP 401", response);
        Assert.Contains("/auth/microsoft?tier=licenses", response);
        Assert.DoesNotContain("tier=chargeback", response);
        Assert.DoesNotContain("tier=base", response);
    }
}
