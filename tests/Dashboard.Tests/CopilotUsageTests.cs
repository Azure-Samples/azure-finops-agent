using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class CopilotUsageTests
{
    private const string Header = "Report Refresh Date,User Principal Name,Display Name,Last Activity Date,Report Period\n";

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
