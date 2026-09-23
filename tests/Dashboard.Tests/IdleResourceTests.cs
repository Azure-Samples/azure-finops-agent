using System.Globalization;
using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;

namespace Dashboard.Tests;

public sealed class IdleResourceTests
{
    [Fact]
    public void EmptyInventoryRetainsUnknownMoneyScopeAndRequestedScriptGuidance()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            var before = DateTime.UtcNow.AddSeconds(-1);
            var report = JsonSerializer.SerializeToElement(IdleResourceTools.BuildReport(
                ["synthetic"], 25, new Dictionary<string, object>
                {
                    ["unattached_disks"] = new { count = 0, items = Array.Empty<object>() }
                }));
            var generated = DateTime.ParseExact(report.GetProperty("generated_utc").GetString()!,
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            Assert.InRange(generated, before, DateTime.UtcNow);
            Assert.True(DateTimeOffset.TryParseExact(report.GetProperty("retrievedAtUtc").GetString(), "o",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            Assert.Equal("synthetic", report.GetProperty("subscriptions_scoped").GetString());
            Assert.Equal(25, report.GetProperty("limitPerPattern").GetInt32());
            Assert.False(report.GetProperty("countsAreEstateTotals").GetBoolean());
            Assert.Equal(JsonValueKind.Null, report.GetProperty("monthlyWaste").ValueKind);
            Assert.Equal(JsonValueKind.Null, report.GetProperty("currency").ValueKind);
            Assert.Equal(JsonValueKind.Null, report.GetProperty("dataAsOfUtc").ValueKind);
            Assert.Contains("indexing delay are unknown", report.GetProperty("freshness").GetString());
            Assert.Contains("read-only revalidation/no-op script", report.GetProperty("scriptGuidance").GetString());
            Assert.Contains("A follow-up link is not the requested artifact", report.GetProperty("scriptGuidance").GetString());
            Assert.Equal(0, report.GetProperty("patterns").GetProperty("unattached_disks").GetProperty("count").GetInt32());
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void IdleToolDoesNotSubstituteFutureOffersForExplicitScriptRequests()
    {
        var tool = new IdleResourceTools(new UserTokens { UserId = 101 }).Create().Single();
        Assert.Contains("call GenerateScript in this turn", tool.Description);
        Assert.Contains("no mutation or invented resource targets", tool.Description);
        Assert.Contains("billing currency", tool.Description);
    }

    [Theory]
    [InlineData("HTTP 200 OK\n{}", "invalid inventory response")]
    [InlineData("HTTP 200 OK\nnull", "invalid inventory response")]
    [InlineData("HTTP 200 OK\n{\"data\":null}", "invalid inventory response")]
    [InlineData("HTTP 200 OK\n{\"data\":[1]}", "invalid inventory response")]
    [InlineData("HTTP 200 OK\n{", "parse failed")]
    [InlineData("HTTP 403 Forbidden\nDenied", "query failed")]
    public void FailedOrMalformedInventoryIsNeverAnEmptySuccessfulScan(string response, string error)
    {
        var parsed = JsonSerializer.SerializeToElement(IdleResourceTools.ParseResourceGraphResponse(response));
        Assert.Equal(error, parsed.GetProperty("error").GetString());
        Assert.False(parsed.TryGetProperty("count", out _));
        Assert.False(parsed.TryGetProperty("items", out _));
    }

    [Theory]
    [InlineData("""{"data":[],"resultTruncated":"false"}""", true)]
    [InlineData("""{"data":[],"resultTruncated":true}""", false)]
    [InlineData("""{"data":[],"resultTruncated":"false","$skipToken":"next"}""", false)]
    [InlineData("""{"data":[]}""", null)]
    public void InventoryKeepsPartialAndUnknownSourceCoverage(string body, bool? complete)
    {
        var parsed = JsonSerializer.SerializeToElement(IdleResourceTools.ParseResourceGraphResponse("HTTP 200 OK\n" + body));
        Assert.Equal(0, parsed.GetProperty("count").GetInt32());
        if (complete is null)
            Assert.Equal(JsonValueKind.Null, parsed.GetProperty("complete").ValueKind);
        else
            Assert.Equal(complete.Value, parsed.GetProperty("complete").GetBoolean());
    }
}
