using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;
using static AzureFinOps.Dashboard.AI.Tools.AzureQueryTools;

namespace Dashboard.Tests;

public sealed class QueryAzureRoutingTests
{
    private static readonly UserTokens Tokens = new()
    {
        UserId = 101,
        AzureToken = "synthetic-test-only",
        GraphToken = "synthetic-test-only",
        LogAnalyticsToken = "synthetic-test-only",
        StorageToken = "synthetic-test-only",
    };

    private static async Task<string> InvokeAsync(AIFunctionArguments arguments)
    {
        var result = await new AzureQueryTools(Tokens).Create().Single().InvokeAsync(arguments);
        return result is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString()! : result!.ToString()!;
    }

    [Fact]
    public void OneToolCoversEveryEndpoint()
    {
        var tool = Assert.Single(new AzureQueryTools(Tokens).Create());
        Assert.Equal("QueryAzure", tool.Name);
        Assert.False(tool.JsonSchema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0);
        foreach (var parameter in new[] { "url", "method", "body", "requests", "resultQuery", "maxPages", "grepFor" })
            Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty(parameter, out _), parameter);
    }

    [Theory]
    [InlineData("/subscriptions/s/providers?api-version=2021-04-01", "Arm")]
    [InlineData("https://management.azure.com/providers/Microsoft.Billing/billingAccounts?api-version=2024-04-01", "Arm")]
    [InlineData("https://graph.microsoft.com/v1.0/subscribedSkus", "Graph")]
    [InlineData("https://GRAPH.microsoft.com/v1.0/me", "Graph")]
    [InlineData("https://api.loganalytics.io/v1/workspaces/w/query", "LogAnalytics")]
    [InlineData("https://api.loganalytics.azure.com/v1/workspaces/w/query", "LogAnalytics")]
    [InlineData("https://api.applicationinsights.io/v1/apps/a/query", "LogAnalytics")]
    [InlineData("https://exports01.blob.core.windows.net/c?restype=container&comp=list", "Storage")]
    [InlineData("https://prices.azure.com/api/retail/prices?$filter=x", "RetailPrices")]
    [InlineData("https://graph.microsoft.com.example.com/v1.0/me", "PublicWeb")]
    [InlineData("https://management.azure.com.example.com/subscriptions", "PublicWeb")]
    [InlineData("https://exports01.blob.core.windows.net.example.com/c", "PublicWeb")]
    [InlineData("https://a.b.blob.core.windows.net/c", "PublicWeb")]
    [InlineData("https://learn.microsoft.com/api/search?search=spot", "PublicWeb")]
    public void CredentialsAreChosenFromTheExactHostOnly(string url, string expected) =>
        Assert.Equal(expected, Classify(ResolveTarget(url)!).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("//example.com/steal")]
    [InlineData("http://graph.microsoft.com/v1.0/me")]
    [InlineData("https://user:secret@graph.microsoft.com/v1.0/me")]
    [InlineData("https://graph.microsoft.com:8443/v1.0/me")]
    [InlineData("https://graph.microsoft.com/v1.0/me#fragment")]
    [InlineData("/subscriptions\\x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("subscriptions/s")]
    public void UnsafeOrAmbiguousTargetsAreRejected(string url) =>
        Assert.Null(ResolveTarget(url));

    [Fact]
    public void ArmPathsArePinnedToResourceManager()
    {
        var uri = ResolveTarget("/subscriptions/s/resourceGroups?api-version=2021-04-01")!;
        Assert.Equal("management.azure.com", uri.Host);
        Assert.Equal("/subscriptions/s/resourceGroups?api-version=2021-04-01", uri.PathAndQuery);
    }

    [Fact]
    public void BatchesAcceptUrlOrPathObjectBodiesAndAWrapper()
    {
        var items = ParseBatch("""{"requests":[{"URL":"/a"},{"method":"POST","path":"/b","body":{"query":"x"}},{"url":"/c","body":"{\"k\":1}"}]}""");
        Assert.Equal(3, items.Count);
        Assert.Equal(("GET", "/a", (string?)null), (items[0].Method, items[0].Path, items[0].Body));
        Assert.Equal(("POST", "/b"), (items[1].Method, items[1].Path));
        Assert.Equal("x", JsonDocument.Parse(items[1].Body!).RootElement.GetProperty("query").GetString());
        Assert.Equal("{\"k\":1}", items[2].Body);
    }

    [Theory]
    [InlineData("[]", "non-empty")]
    [InlineData("[null]", "request object")]
    [InlineData("[\"/a\"]", "request object")]
    [InlineData("{not json", "complete JSON array")]
    [InlineData("{\"url\":\"/a\"}", "non-empty JSON array")]
    public void MalformedBatchesAreRejected(string requests, string error) =>
        Assert.Contains(error, Assert.Throws<FormatException>(() => ParseBatch(requests)).Message);

    [Fact]
    public void OversizedBatchesAreRejected() =>
        Assert.Contains("at most 200", Assert.Throws<FormatException>(() =>
            ParseBatch(JsonSerializer.Serialize(Enumerable.Range(0, 201).Select(index => new { url = "/x" + index })))).Message);

    [Theory]
    [InlineData("{}", "exactly one of url")]
    [InlineData("{\"url\":\"/a\",\"requests\":\"[{\\\"url\\\":\\\"/b\\\"}]\"}", "exactly one of url")]
    [InlineData("{\"requests\":\"[null]\"}", "request object")]
    [InlineData("{\"url\":\"http://example.com/x\"}", "absolute https://")]
    [InlineData("{\"url\":\"https://example.com/x\",\"method\":\"POST\"}", "GET only")]
    [InlineData("{\"url\":\"https://169.254.169.254/metadata/instance\"}", "not reachable")]
    [InlineData("{\"url\":\"https://graph.microsoft.com/me\"}", "/v1.0/ or /beta/")]
    [InlineData("{\"url\":\"https://api.loganalytics.io/workspaces/w/query\",\"method\":\"POST\"}", "/v1/workspaces")]
    [InlineData("{\"url\":\"https://api.loganalytics.io/v1/workspaces/w/query\",\"method\":\"PUT\"}", "GET or POST")]
    [InlineData("{\"url\":\"https://exports01.blob.core.windows.net/c/b.csv\",\"method\":\"PUT\"}", "read-only")]
    [InlineData("{\"url\":\"https://prices.azure.com/api/retail/prices?currencyCode=USD\"}", "$filter")]
    [InlineData("{\"url\":\"https://prices.azure.com/api/retail/prices?currencyCode=US&$filter=serviceName eq 'Storage'\"}", "three-letter")]
    [InlineData("{\"url\":\"https://prices.azure.com/api/retail/prices?$filter=x\",\"method\":\"POST\"}", "GET only")]
    [InlineData("{\"url\":\"https://prices.azure.com/other\"}", "api/retail/prices")]
    [InlineData("{\"url\":\"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm/start?api-version=2024-07-01\",\"method\":\"POST\"}", "read-only Azure POST")]
    public async Task InvalidRequestsFailBeforeAnyNetworkCall(string arguments, string error)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(arguments)!;
        var result = await InvokeAsync(new AIFunctionArguments(parsed.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        Assert.StartsWith("HTTP 4", result);
        Assert.Contains(error, result);
        Assert.False(ProtectedTool.InspectEvidence(result).Success);
    }

    [Fact]
    public async Task DeleteIsBlockedForEveryService()
    {
        foreach (var url in new[] { "/subscriptions/s/resourceGroups/rg?api-version=2021-04-01", "https://graph.microsoft.com/v1.0/users/u" })
        {
            var result = await InvokeAsync(new AIFunctionArguments { ["url"] = url, ["method"] = "DELETE" });
            Assert.Contains("DELETE", result, StringComparison.OrdinalIgnoreCase);
            Assert.False(ProtectedTool.InspectEvidence(result).Success);
        }
    }

    [Fact]
    public void RetailUrlKeepsTheModelFilterAndPinsTheOrigin()
    {
        const string filter = "serviceName eq 'Synthetic' and contains(skuName, 'model&name')";
        var source = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery("?$top=5&$skip=100&meterRegion='primary'");
        var uri = new Uri(BuildRetailUrl(source, filter, "CAD"));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

        Assert.Equal("https://prices.azure.com/api/retail/prices", uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("2023-01-01-preview", query["api-version"].ToString());
        Assert.Equal("CAD", query["currencyCode"].ToString());
        Assert.Equal(filter, query["$filter"].ToString());
        Assert.Equal("'primary'", query["meterRegion"].ToString());
        Assert.False(query.ContainsKey("$top"));
        Assert.False(query.ContainsKey("$skip"));
    }

    [Fact]
    public async Task SameHostPagesAreMergedWithCoverage()
    {
        var fetched = new List<string>();
        var result = await PaginateAsync(
            "HTTP 200 OK\nCurrent UTC time: 2026-09-25 10:00:00\n{\"value\":[1],\"nextLink\":\"https://management.azure.com/next?page=2\"}",
            new Uri("https://management.azure.com/first"), 5,
            next =>
            {
                fetched.Add(next.AbsoluteUri);
                return Task.FromResult("HTTP 200 OK\n{\"value\":[2,3]}");
            });
        var (preamble, body) = ResponseShaper.SplitPreamble(result);
        Assert.Equal("HTTP 200 OK\nCurrent UTC time: 2026-09-25 10:00:00\n", preamble);
        using var json = JsonDocument.Parse(body);
        Assert.Equal([1, 2, 3], json.RootElement.GetProperty("value").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal(2, json.RootElement.GetProperty("pagesRead").GetInt32());
        Assert.True(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.Single(fetched);
        Assert.Equal((true, true, false), ProtectedTool.InspectEvidence(result));
    }

    [Fact]
    public void GraphRequestsEnableDirectoryAdvancedQueries()
    {
        var headers = AzureQueryTools.GraphHeaders();
        Assert.Equal("eventual", headers["consistencylevel"]);
        Assert.Single(headers);
        Assert.Contains("$count=true", AzureQueryTools.ToolDescription);
    }

    [Fact]
    public async Task PageLimitsAndLaterFailuresReportPartialCoverageWithoutAnError()
    {
        const string first = "HTTP 200 OK\n{\"value\":[1],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users?$skiptoken=a\"}";
        var origin = new Uri("https://graph.microsoft.com/v1.0/users");

        var limited = await PaginateAsync(first, origin, 1, _ => throw new InvalidOperationException("must not fetch"));
        using (var json = JsonDocument.Parse(ResponseShaper.SplitPreamble(limited).Body))
        {
            Assert.False(json.RootElement.GetProperty("complete").GetBoolean());
            Assert.StartsWith("https://graph.microsoft.com/", json.RootElement.GetProperty("@odata.nextLink").GetString());
        }

        var failed = await PaginateAsync(first, origin, 5, _ => Task.FromResult("HTTP 503 ServiceUnavailable\n{\"error\":{\"code\":\"x\"}}"));
        using (var json = JsonDocument.Parse(ResponseShaper.SplitPreamble(failed).Body))
        {
            Assert.Single(json.RootElement.GetProperty("value").EnumerateArray());
            Assert.Contains("HTTP 503", json.RootElement.GetProperty("pageFailure").GetString());
            Assert.False(json.RootElement.TryGetProperty("error", out _));
        }
        Assert.Equal((true, true, true), ProtectedTool.InspectEvidence(failed));
    }

    [Fact]
    public async Task CrossHostNextLinksAreNeverFollowed()
    {
        const string response = "HTTP 200 OK\n{\"value\":[1],\"nextLink\":\"https://example.com/steal\"}";
        var result = await PaginateAsync(response, new Uri("https://management.azure.com/x"), 5, _ => throw new InvalidOperationException("must not fetch"));
        Assert.Equal(response, result);
    }

    [Fact]
    public void CsvBecomesATypedColumnarTable()
    {
        var json = ResponseShaper.CsvToJson("\uFEFFUser Principal Name,Last Activity Date,Seats,Id\r\n\"a,b@contoso.com\",2026-09-01,3,007\r\nc@contoso.com,,,12\r\n")!;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("csv", root.GetProperty("format").GetString());
        Assert.Equal(["string", "string", "number", "string"], root.GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("type").GetString()));
        Assert.Equal(2, root.GetProperty("rowCount").GetInt32());
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Equal("a,b@contoso.com", root.GetProperty("rows")[0][0].GetString());
        Assert.Equal(3, root.GetProperty("rows")[0][2].GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rows")[1][2].ValueKind);
        Assert.Equal("007", root.GetProperty("rows")[0][3].GetString());
    }

    [Fact]
    public void TruncatedCsvDropsThePartialRowAndReportsIncompleteCoverage()
    {
        using var document = JsonDocument.Parse(ResponseShaper.CsvToJson("Name,Cost\nvm1,1\nvm2,2\nvm", truncated: true)!);
        Assert.Equal(2, document.RootElement.GetProperty("rowCount").GetInt32());
        Assert.False(document.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal((true, true, true), ProtectedTool.InspectEvidence(document.RootElement.GetRawText()));
    }

    [Fact]
    public void XmlListingsBecomeJsonAndDtdsAreRejected()
    {
        var json = ResponseShaper.XmlToJson("<?xml version=\"1.0\"?><EnumerationResults ContainerName=\"c\"><Blobs><Blob><Name>a.csv</Name></Blob><Blob><Name>b.csv</Name></Blob></Blobs><NextMarker /></EnumerationResults>")!;
        using var document = JsonDocument.Parse(json);
        var results = document.RootElement.GetProperty("EnumerationResults");
        Assert.Equal("c", results.GetProperty("@ContainerName").GetString());
        Assert.Equal(2, results.GetProperty("Blobs").GetProperty("Blob").GetArrayLength());

        Assert.Null(ResponseShaper.XmlToJson("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>"));
        Assert.Null(ResponseShaper.XmlToJson("<!doctype html><html><body>x</body></html>"));
    }

    [Theory]
    [InlineData("HTTP 200 OK\nName,Cost\nvm1,1\n", true)]
    [InlineData("HTTP 200 OK\n{\"a\":1}", false)]
    [InlineData("HTTP 404 NotFound\nName,Cost\nvm1,1\n", false)]
    [InlineData("HTTP 200 OK\nplain text", false)]
    public void NormalizeConvertsOnlySuccessfulNonJsonBodies(string response, bool converted)
    {
        var result = ResponseShaper.Normalize(response);
        Assert.Equal(converted, result != response);
        if (converted) Assert.StartsWith("HTTP 200 OK\n{\"format\":\"csv\"", result);
    }

    [Theory]
    [InlineData("{\"source\":{\"resourceId\":\"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm\"},\"destination\":{\"address\":\"contoso.com\",\"port\":443},\"protocol\":\"Tcp\"}", true)]
    [InlineData("{\"source\":{\"resourceId\":\"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm\"},\"destination\":{\"address\":\"10.0.0.4\",\"port\":\"1433\"}}", true)]
    [InlineData("{\"source\":{\"resourceId\":\"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Network/applicationGateways/ag\"},\"destination\":{\"address\":\"contoso.com\",\"port\":443}}", false)]
    [InlineData("{\"source\":{\"resourceId\":\"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm\"},\"destination\":{\"port\":443}}", false)]
    [InlineData("{\"source\":{\"resourceId\":\"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm\"},\"destination\":{\"address\":\"contoso.com\",\"port\":70000}}", false)]
    [InlineData("{\"source\":{\"resourceId\":\"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm\"},\"destination\":{\"address\":\"contoso.com\",\"port\":443},\"packetCapture\":true}", false)]
    [InlineData("not json", false)]
    public void ConnectivityChecksOnlyProbeFromAnExistingVm(string body, bool valid) =>
        Assert.Equal(valid, ValidateConnectivityBody(body) is null);

    [Theory]
    [InlineData("{\"subscriptions\":[\"11111111-1111-1111-1111-111111111111\"],\"query\":\"Resources\"}", "/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01")]
    [InlineData("{\"subscriptions\":[\"22222222-2222-2222-2222-222222222222\"],\"query\":\"Resources\"}", null)]
    [InlineData("{\"managementGroups\":[\"mg\"],\"query\":\"Resources\"}", null)]
    [InlineData("{\"query\":\"Resources\"}", null)]
    [InlineData("not json", null)]
    public void SubscriptionPrefixedResourceGraphPathsDropOnlyAPrefixTheBodyAlreadyScopes(string body, string? expected)
    {
        const string path = "/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01";
        Assert.Equal(expected ?? path, CanonicalResourceGraphPath(path, body));
        const string grouped = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01";
        Assert.Equal(grouped, CanonicalResourceGraphPath(grouped, body));
    }

    [Fact]
    public void QueryBodiesMissingOnlyTheirFinalBraceAreClosed()
    {
        var closed = CanonicalJsonBody("{\"subscriptions\":[\"11111111-1111-1111-1111-111111111111\"],\"query\":\"Resources | take 5\"");
        Assert.Null(ValidateQueryBody("/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01", closed));
        Assert.Equal("{\"query\":\"Resources", CanonicalJsonBody("{\"query\":\"Resources"));
        Assert.Equal("[1,2", CanonicalJsonBody("[1,2"));
    }

    [Theory]
    [InlineData("HTTP 404 NotFound\n{\"error\":{\"code\":\"InvalidResourceType\",\"message\":\"The resource type 'ScheduledActions' could not be found in the namespace 'Microsoft.CostManagement' for api version '2023-08-01-preview'. The supported api-versions are '2022-04-01-preview,2023-08-01,2024-10-01-preview,2025-03-01'.\"}}", "2025-03-01")]
    [InlineData("HTTP 400 BadRequest\n{\"error\":{\"code\":\"InvalidApiVersionParameter\",\"message\":\"The api-version '2023-08-01-preview' is invalid. The supported versions are '2024-01-01-preview,2024-06-01-preview'.\"}}", "2024-06-01-preview")]
    [InlineData("HTTP 400 BadRequest\n{\"error\":{\"code\":\"NoRegisteredProviderFound\",\"message\":\"No registered resource provider found for location 'global' and API version '2023-08-01-preview' for type 'x'. The supported api-versions are '2023-08-01-preview'.\"}}", null)]
    [InlineData("HTTP 400 BadRequest\n{\"error\":{\"code\":\"BadRequest\",\"message\":\"The supported api-versions are '2025-03-01'.\"}}", null)]
    [InlineData("HTTP 404 NotFound\n{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"The resource was not found.\"}}", null)]
    [InlineData("HTTP 200 OK\n{\"value\":[]}", null)]
    public void RejectedApiVersionsResolveToTheNewestSupportedVersion(string response, string? expected) =>
        Assert.Equal(expected, SupportedApiVersion("/subscriptions/x/providers/Microsoft.CostManagement/scheduledActions?api-version=2023-08-01-preview", response));

    [Fact]
    public void CorrectedApiVersionsAreRewrittenAndAnnotated()
    {
        Assert.Equal("/subscriptions/x/providers/A/b?$top=5&api-version=2025-03-01&x=1",
            WithApiVersion("/subscriptions/x/providers/A/b?$top=5&api-version=2023-01-01-preview&x=1", "2025-03-01"));
        var annotated = AnnotateApiVersion("HTTP 200 OK\nCurrent UTC time: 2026-01-01 00:00:00\n{\"value\":[]}", "2023-08-01-preview", "2025-03-01");
        Assert.StartsWith("HTTP 200 OK\nCurrent UTC time: 2026-01-01 00:00:00\n", annotated);
        using var document = JsonDocument.Parse(ResponseShaper.SplitPreamble(annotated).Body);
        Assert.Equal("2025-03-01", document.RootElement.GetProperty("_apiVersion").GetProperty("used").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("value").GetArrayLength());
        using var empty = JsonDocument.Parse(ResponseShaper.SplitPreamble(AnnotateApiVersion("HTTP 200 OK\n{}", "a", "b")).Body);
        Assert.Equal("a", empty.RootElement.GetProperty("_apiVersion").GetProperty("requested").GetString());
        Assert.Equal("HTTP 200 OK\n[1]", AnnotateApiVersion("HTTP 200 OK\n[1]", "a", "b"));
    }

    [Fact]
    public void RedundantCurrencyDimensionGroupingIsRemovedVisiblyFromCostQueriesOnly()
    {
        const string query = "/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.CostManagement/query?api-version=2025-03-01";
        var (body, removed) = WithoutCurrencyGrouping(query,
            """{"type":"ActualCost","dataset":{"grouping":[{"type":"Dimension","name":"ServiceName"},{"type":"dimension","name":"currency"},{"type":"TagKey","name":"Currency"}]}}""");
        Assert.True(removed);
        using (var document = JsonDocument.Parse(body!))
        {
            var grouping = document.RootElement.GetProperty("dataset").GetProperty("grouping");
            Assert.Equal(["Dimension:ServiceName", "TagKey:Currency"],
                grouping.EnumerateArray().Select(item => item.GetProperty("type").GetString() + ":" + item.GetProperty("name").GetString()));
            Assert.Equal("ActualCost", document.RootElement.GetProperty("type").GetString());
        }
        Assert.Null(ValidateQueryBody(query, body));

        const string unchanged = """{"dataset":{"grouping":[{"type":"TagKey","name":"Currency"}]}}""";
        Assert.Equal((unchanged, false), WithoutCurrencyGrouping(query, unchanged));
        const string grouped = """{"dataset":{"grouping":[{"type":"Dimension","name":"Currency"}]}}""";
        Assert.Equal((grouped, false), WithoutCurrencyGrouping(query.Replace("/query", "/forecast"), grouped));
        Assert.Equal(("not json", false), WithoutCurrencyGrouping(query, "not json"));

        var annotated = AnnotateRoot("HTTP 200 OK\n{\"properties\":{}}", "_request", new Dictionary<string, string> { ["removedGrouping"] = "Currency" });
        using var result = JsonDocument.Parse(ResponseShaper.SplitPreamble(annotated).Body);
        Assert.Equal("Currency", result.RootElement.GetProperty("_request").GetProperty("removedGrouping").GetString());
        Assert.True(result.RootElement.TryGetProperty("properties", out _));
    }

    [Theory]
    [InlineData("HTTP 400 BadRequest\n{\"error\":{\"code\":\"UnsupportedApiVersion\",\"message\":\"The HTTP resource that matches the request URI 'x' does not support the API version '2026-08-01'.\",\"innerError\":null}}", true)]
    [InlineData("HTTP 400 BadRequest\n{\"error\":{\"code\":\"UnsupportedApiVersion\",\"message\":\"The supported api-versions are '2025-03-01'.\"}}", false)]
    [InlineData("HTTP 400 BadRequest\n{\"error\":{\"code\":\"BadRequest\",\"message\":\"Invalid body.\"}}", false)]
    [InlineData("HTTP 404 NotFound\n{\"error\":{\"code\":\"UnsupportedApiVersion\",\"message\":\"x\"}}", false)]
    [InlineData("HTTP 400 BadRequest\nnot json", false)]
    public void ProviderApiVersionRejectionsWithoutAListAreRecognized(string response, bool expected) =>
        Assert.Equal(expected, IsUnlistedApiVersionRejection(response));

    [Theory]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.CostManagement/scheduledActions?api-version=2026-08-01", "/subscriptions/11111111-1111-1111-1111-111111111111", "Microsoft.CostManagement", "scheduledActions")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm/extensions?api-version=2024-07-01", "/subscriptions/11111111-1111-1111-1111-111111111111", "Microsoft.Compute", "virtualMachines/extensions")]
    [InlineData("/providers/Microsoft.Billing/billingAccounts/a/providers/Microsoft.CostManagement/exports/e?api-version=2026-08-01", "", "Microsoft.CostManagement", "exports")]
    public void ResourceTypesComeFromTheLastProviderSegment(string path, string scope, string provider, string type) =>
        Assert.Equal((scope, provider, type), ResourceTypeOf(path));

    [Theory]
    [InlineData("/subscriptions/x/resourcegroups?api-version=2021-04-01")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.CostManagement?api-version=2021-04-01")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.CostManagement/a%2Fb?api-version=1")]
    public void PathsWithoutAResourceTypeHaveNone(string path) => Assert.Null(ResourceTypeOf(path));

    [Fact]
    public void OlderStableManifestVersionsAreTriedNewestFirst()
    {
        const string manifest = "HTTP 200 OK\n{\"namespace\":\"Microsoft.CostManagement\",\"resourceTypes\":[{\"resourceType\":\"Exports\",\"apiVersions\":[\"2026-08-01\"]},{\"resourceType\":\"ScheduledActions\",\"apiVersions\":[\"2026-08-01\",\"2026-06-01\",\"2025-03-01\",\"2024-10-01-preview\",\"2024-08-01\"]}]}";
        Assert.Equal(["2026-06-01", "2025-03-01"], OlderStableVersions(manifest, "scheduledActions", "2026-08-01"));
        Assert.Equal(["2024-08-01"], OlderStableVersions(manifest, "scheduledActions", "2025-03-01"));
        Assert.Empty(OlderStableVersions(manifest, "exports", "2026-08-01"));
        Assert.Empty(OlderStableVersions(manifest, "views", "2026-08-01"));
        Assert.Empty(OlderStableVersions("HTTP 403 Forbidden\n{}", "scheduledActions", "2026-08-01"));
    }

    [Theory]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.PolicyInsights/policyStates/latest/summarize?api-version=2019-10-01", true)]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.PolicyInsights/policyStates/default/queryResults?api-version=2019-10-01", true)]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg/providers/Microsoft.PolicyInsights/policyEvents/default/queryResults?api-version=2019-10-01", true)]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.PolicyInsights/policyStates/latest/triggerEvaluation?api-version=2019-10-01", false)]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.PolicyInsights/remediations/r/cancel?api-version=2021-10-01", false)]
    [InlineData("/providers/Microsoft.PolicyInsights/policyStates/latest/summarize?api-version=2019-10-01", false)]
    public void PolicyComplianceQueriesAreReadOnlyPosts(string path, bool allowed) =>
        Assert.Equal(allowed, ValidateReadOnlyPostPath(path, null) is null);
}
