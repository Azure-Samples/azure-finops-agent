using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class ThinToolBoundaryTests
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
        Assert.True(AzureQueryTools.IsReportServiceAbsent(path, UnknownTenant));

        var result = AzureQueryTools.ReportServiceAbsent(UnknownTenant, null);
        var lines = result.Split('\n');
        Assert.Equal("HTTP 200 OK", lines[0].Trim());
        Assert.Equal("Current UTC time: 2026-09-25 20:20:56", lines[1].Trim());
        using var json = JsonDocument.Parse(lines[2]);
        Assert.False(json.RootElement.GetProperty("reportServiceProvisioned").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("error", out _));
        Assert.True(EvidenceInspector.Inspect(result).Success);
    }

    [Theory]
    [InlineData("/v1.0/users", "HTTP 404 NotFound\n{\"error\":{\"code\":\"UnknownTenantId\"}}")]
    [InlineData("/v1.0/reports/getOffice365ActiveUserDetail(period='D30')", "HTTP 404 NotFound\n{\"error\":{\"code\":\"ResourceNotFound\"}}")]
    [InlineData("/v1.0/reports/getOffice365ActiveUserDetail(period='D30')", "HTTP 403 Forbidden\n{\"error\":{\"code\":\"UnknownTenantId\"}}")]
    public void OtherFailuresStayFailures(string path, string result) =>
        Assert.False(AzureQueryTools.IsReportServiceAbsent(path, result));

    [Fact]
    public void JsonObjectForStringParameterIsPassedAsRawJson()
    {
        using var schema = JsonDocument.Parse("""{"properties":{"queryJson":{"type":"string"},"limit":{"type":"string"}}}""");
        using var argument = JsonDocument.Parse("""{"mode":"query","path":"$.value[*]"}""");
        var arguments = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["queryJson"] = argument.RootElement.Clone(),
            ["limit"] = JsonDocument.Parse("5").RootElement.Clone(),
        };
        ModelJson.CoerceScalarStrings(schema.RootElement, arguments);
        Assert.Equal("""{"mode":"query","path":"$.value[*]"}""", arguments["queryJson"]);
        Assert.Equal("5", arguments["limit"]);
    }

    [Fact]
    public void ACallEnvelopeNamingTheInvokedToolIsUnwrapped()
    {
        using var envelope = JsonDocument.Parse("""{"resultId":"abc","queryJson":"{\"path\":\"$\"}"}""");
        var arguments = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["recipient_name"] = "functions.QueryUploadedFile",
            ["parameters"] = envelope.RootElement.Clone(),
        };
        ModelJson.UnwrapCallEnvelope("QueryUploadedFile", arguments);
        Assert.Equal(["queryJson", "resultId"], arguments.Keys.Order());
        Assert.Equal("abc", ((JsonElement)arguments["resultId"]!).GetString());

        var stringified = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["recipient_name"] = JsonDocument.Parse("\"QueryUploadedFile\"").RootElement.Clone(),
            ["parameters"] = """{"resultId":"abc"}""",
        };
        ModelJson.UnwrapCallEnvelope("QueryUploadedFile", stringified);
        Assert.Equal(["resultId"], stringified.Keys);
    }

    [Theory]
    [InlineData("functions.QueryAzure")]
    [InlineData("functions.QueryUploadedFile.extra")]
    [InlineData("")]
    public void EnvelopesForOtherToolsOrWithExtraKeysAreLeftForBindingToReject(string recipient)
    {
        var arguments = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["recipient_name"] = recipient,
            ["parameters"] = JsonDocument.Parse("""{"resultId":"abc"}""").RootElement.Clone(),
        };
        ModelJson.UnwrapCallEnvelope("QueryUploadedFile", arguments);
        Assert.True(arguments.ContainsKey("recipient_name"));

        var extra = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["recipient_name"] = "functions.QueryUploadedFile",
            ["parameters"] = JsonDocument.Parse("""{"resultId":"abc"}""").RootElement.Clone(),
            ["resultId"] = "other",
        };
        ModelJson.UnwrapCallEnvelope("QueryUploadedFile", extra);
        Assert.Equal("other", extra["resultId"]);
        Assert.True(extra.ContainsKey("parameters"));
    }

    [Fact]
    public void ScoresAreReadAsJsonWithoutRepair()
    {
        const string item = """[{"id":"tagging","label":"Tagging","status":"observed","score":3,"detail":"45% tagged"}]""";
        Assert.NotNull(ScoreTools.NormalizeScores(item));
        Assert.Equal(ScoreTools.NormalizeScores(item), ScoreTools.NormalizeScores(" " + item + "\n"));
        Assert.Equal(ScoreTools.NormalizeScores(item), ScoreTools.NormalizeScores(ModelJson.Text(JsonDocument.Parse(item).RootElement)));
        Assert.Null(ScoreTools.NormalizeScores(item + "}"));
        Assert.Null(ScoreTools.NormalizeScores(item[..^1]));
        Assert.Null(ScoreTools.NormalizeScores(item + "x"));
        Assert.Null(ScoreTools.NormalizeScores("""{"id":"tagging"}"""));
    }
}