using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class GraphReportServiceTests
{
    private const string UnknownTenant = """
        HTTP 404 NotFound
        Current UTC time: 2026-09-25 20:20:56
        {"error":{"code":"UnknownError","message":"{\"error\":{\"code\":\"UnknownTenantId\",\"message\":\"We do not recognize this tenant ID.\"}}"}}
        """;

    [Fact]
    public void UnprovisionedReportServiceIsADeterminateResult()
    {
        const string path = "/v1.0/copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='D30')";
        Assert.True(GraphQueryTools.IsReportServiceAbsent(path, UnknownTenant));

        var result = GraphQueryTools.ReportServiceAbsent(UnknownTenant, null);
        var lines = result.Split('\n');
        Assert.Equal("Current UTC time: 2026-09-25 20:20:56", lines[0].Trim());
        using var json = JsonDocument.Parse(lines[1]);
        Assert.False(json.RootElement.GetProperty("reportServiceProvisioned").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData("/v1.0/users", "HTTP 404 NotFound\n{\"error\":{\"code\":\"UnknownTenantId\"}}")]
    [InlineData("/v1.0/reports/getOffice365ActiveUserDetail(period='D30')", "HTTP 404 NotFound\n{\"error\":{\"code\":\"ResourceNotFound\"}}")]
    [InlineData("/v1.0/reports/getOffice365ActiveUserDetail(period='D30')", "HTTP 403 Forbidden\n{\"error\":{\"code\":\"UnknownTenantId\"}}")]
    public void OtherFailuresStayFailures(string path, string result) =>
        Assert.False(GraphQueryTools.IsReportServiceAbsent(path, result));
}
