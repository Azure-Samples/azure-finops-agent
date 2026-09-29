using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;

namespace Dashboard.Tests;

public sealed class JsonQueryTests
{
    private const string Vms = """
        {"value":[
          {"name":"vm-a","location":"eastus","tags":{"env":"prod"},"properties":{"hardwareProfile":{"vmSize":"Standard_D4s_v5"}},"cost":12.5},
          {"name":"vm-b","location":"westus","tags":{},"properties":{"hardwareProfile":{"vmSize":"Standard_D2s_v5"}},"cost":null},
          {"name":"vm-c","location":"eastus","properties":{"hardwareProfile":{"vmSize":"Standard_D4s_v5"}},"cost":7.25}
        ],"nextLink":"https://management.azure.com/next"}
        """;

    private static string Run(string body, string query, int maxCharacters = 48 * 1024) =>
        JsonQuery.Evaluate(JsonQuery.Parse(body), query, maxCharacters, CancellationToken.None).Json;

    [Fact]
    public void SchemaShowsTheInferredShapeWithAStarterExpression()
    {
        var schema = JsonQuery.Schema(JsonQuery.Parse(Vms));

        Assert.StartsWith("it: ", schema);
        Assert.Contains("value: ", schema);
        Assert.Contains("vmSize", schema);
        Assert.Contains("tags", schema);
        Assert.Contains("nextLink", schema);
        Assert.Contains("\nExample: ", schema);
    }

    [Fact]
    public void CSharpProjectionsFiltersAndAggregatesRun()
    {
        Assert.Equal("""[{"name":"vm-a","size":"Standard_D4s_v5"},{"name":"vm-c","size":"Standard_D4s_v5"}]""",
            Run(Vms, "value.Where(x => x.location == \"eastus\").Select(x => new { x.name, size = x.properties.hardwareProfile.vmSize }).OrderBy(x => x.name)"));
        Assert.Equal("19.75", Run(Vms, "value.Sum(x => x.cost ?? 0)"));
        Assert.Equal("""[{"size":"Standard_D4s_v5","count":2,"total":19.75},{"size":"Standard_D2s_v5","count":1,"total":0}]""",
            Run(Vms, "value.GroupBy(x => x.properties.hardwareProfile.vmSize).Select(g => new { size = g.Key, count = g.Count(), total = g.Sum(x => x.cost ?? 0) }).OrderByDescending(x => x.count)"));
        Assert.Equal("""["prod",null,null]""", Run(Vms, "value.Select(x => x.tags != null && x.tags.ContainsKey(\"env\") ? x.tags[\"env\"] : null)"));
        Assert.Equal("3", Run(Vms, "value.Select(x => root.value.Count()).First()"));
    }

    [Fact]
    public void ColumnRowTablesAndTextBecomeQueryableRows()
    {
        const string cost = """{"properties":{"columns":[{"name":"PreTaxCost","type":"Number"},{"name":"ResourceGroup","type":"String"}],"rows":[[1.5,"a"],[2.5,"b"]]}}""";
        Assert.Equal("""["b"]""", Run(cost, "properties.rows.Where(r => r.PreTaxCost > 2).Select(r => r.ResourceGroup)"));
        Assert.Equal("""["beta"]""", Run("alpha\nbeta\n", "lines.Where(x => x.StartsWith(\"b\"))"));
        Assert.Equal("2", Run("""[{"a":1},{"a":2}]""", "Max(x => x.a)"));
    }

    [Theory]
    [InlineData("new { a = 1, b = \"x = y\" }", "new ((1) as a, (\"x = y\") as b)")]
    [InlineData("value.Select(x => new { x.name })", "value.Select(x => new (x.name))")]
    [InlineData("new { outer = new { inner = 1 } }", "new ((new ((1) as inner)) as outer)")]
    public void CSharpAnonymousObjectsAreRewrittenToDynamicLinq(string csharp, string dynamic) =>
        Assert.Equal(dynamic, JsonQuery.Projections(csharp));

    [Fact]
    public void KeywordAliasesAndInvariantNumbersWork()
    {
        Assert.Contains("_lt (JSON \"lt\"): ", JsonQuery.Schema(JsonQuery.Parse("""{"value":[{"lt":1}]}""")));
        Assert.Equal("""[{"v":1}]""", Run("""{"value":[{"lt":1}]}""", "value.Select(x => new { v = x._lt })"));
        Assert.Equal("""["1.5"]""", Run("""{"value":[{"n":1.5}]}""", "value.Select(x => x.n.ToString())"));
    }

    // Keeps the documented examples in docs/tool-catalog.md runnable.
    [Fact]
    public void ToolCatalogExamplesRun()
    {
        const string retail = """{"Items":[{"productName":"Virtual Machines Dsv5 Series","meterName":"D2s v5","unitPrice":0.096,"unitOfMeasure":"1 Hour","currencyCode":"USD","armSkuName":"Standard_D2s_v5"}],"retrievedAtUtc":"2026-01-01T00:00:00Z","pages":1,"complete":true}""";
        Assert.Equal("""[{"productName":"Virtual Machines Dsv5 Series","meterName":"D2s v5","unitPrice":0.096,"unitOfMeasure":"1 Hour","currencyCode":"USD"}]""",
            Run(retail, "Items.Select(i => new { i.productName, i.meterName, unitPrice = Math.Round(i.unitPrice ?? 0, 4), i.unitOfMeasure, i.currencyCode }).Take(20)"));

        const string usages = """{"value":[{"name":{"value":"cores","localizedValue":"Total Regional vCPUs"},"currentValue":8,"limit":100,"unit":"Count"},{"name":{"value":"standardDSv5Family","localizedValue":"Standard DSv5 Family vCPUs"},"currentValue":4,"limit":50,"unit":"Count"}]}""";
        Assert.Equal("""[{"name":"cores","used":8,"limit":100}]""",
            Run(usages, "value.Where(v => v.name.value == \"cores\" || v.name.value == \"lowPriorityCores\").Select(v => new { name = v.name.value, used = v.currentValue, v.limit })"));

        Assert.Equal("Calculation result (no request was sent):\n{\"monthly\":280.32}", AzureQueryTools.Calculate("new { monthly = Math.Round(0.096 * 730 * 4, 2) }", CancellationToken.None));
    }

    [Fact]
    public void LargeResultsAreTruncatedWithANote()
    {
        var body = JsonSerializer.Serialize(new { value = Enumerable.Range(0, 2000).Select(index => new { name = "resource-" + index }) });
        var (json, note) = JsonQuery.Evaluate(JsonQuery.Parse(body), "value.Select(x => x.name)", 4096, CancellationToken.None);

        Assert.True(json.Length <= 4096);
        using var document = JsonDocument.Parse(json);
        Assert.InRange(document.RootElement.GetArrayLength(), 10, 1999);
        Assert.NotNull(note);
    }

    [Theory]
    [InlineData("value.Select(x => x.GetType())")]
    [InlineData("value.Select(x => x.name.GetType().Assembly)")]
    [InlineData("value.Select(x => x.name.PadLeft(100000000))")]
    [InlineData("value.Select(x => string.Join(\",\", root.value.Select(y => y.name)))")]
    [InlineData("value.Select(x => x.name.Replace(\"a\", x.name))")]
    [InlineData("value.Select(x => x.cost.Value.ToString(\"F999999999\"))")]
    [InlineData("value.Select(x => new string('x', 100000000))")]
    [InlineData("Environment.GetEnvironmentVariable(\"PATH\")")]
    [InlineData("System.IO.File.ReadAllText(\"/etc/passwd\")")]
    [InlineData("Activator.CreateInstance(\"System.Net.WebClient\")")]
    [InlineData("AppDomain.CurrentDomain")]
    [InlineData("typeof(string)")]
    [InlineData("string.Format(\"{0}\", 1)")]
    public void OnlyDataMembersAreCallable(string query) =>
        Assert.ThrowsAny<Exception>(() => Run(Vms, query));

    [Fact]
    public void RunawayQueriesStopAtTheBudget()
    {
        var body = JsonSerializer.Serialize(new { value = Enumerable.Range(0, 5000).Select(index => new { n = index }) });
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => Run(body, "value.SelectMany(a => root.value.SelectMany(b => root.value.Select(c => a.n + b.n + c.n))).Sum()"));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void RootAggregatesInsideLambdasAreEvaluatedOnce()
    {
        var body = JsonSerializer.Serialize(new { value = Enumerable.Range(0, 14_000).Select(index => new { name = $"r{index}", cost = index % 100 }) });
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("""[{"name":"r99","share":0.000143}]""",
            Run(body, "value.Select(x => new { x.name, share = Math.Round((x.cost ?? 0) / root.value.Sum(y => y.cost ?? 0), 6) }).OrderByDescending(x => x.share).Take(1)"));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));

        // Hoisted calls stay lazy: an unreached branch never runs, and calls that read the item are not hoisted.
        Assert.Equal("""["none","none","none"]""", Run(Vms, "value.Select(x => x.cost > 1000 ? root.value.First(y => y.cost > 1000).name : \"none\")"));
        Assert.Equal("""[1,0,1]""", Run(Vms, "value.Select(x => root.value.Count(y => y.location == x.location && y.name != x.name))"));
    }

    [Fact]
    public void CoverageFieldsTravelBesideTheModelsProjection()
    {
        var metadata = JsonQuery.Metadata(JsonQuery.Parse("""{"complete":false,"pagesRead":10,"value":[{"a":1}],"_finops":{"cacheStatus":"queried"}}"""));

        Assert.NotNull(metadata);
        Assert.False(metadata["complete"]!.GetValue<bool>());
        Assert.Equal(10, metadata["pagesRead"]!.GetValue<int>());
        Assert.NotNull(metadata["_finops"]);
        Assert.Null(metadata["value"]);
        Assert.Null(JsonQuery.Metadata(JsonQuery.Parse("""{"value":[]}""")));
    }

    [Fact]
    public void SmallResponsesReturnWholeAndLargeOnesReturnTheirSchema()
    {
        var small = "HTTP 200 OK\nCurrent UTC time: 2026-09-25 20:20:56\n" + Vms;
        Assert.Same(small, AzureQueryTools.Crop(small, null, null, CancellationToken.None));

        var large = "HTTP 200 OK\n" + JsonSerializer.Serialize(new
        {
            complete = false,
            value = Enumerable.Range(0, 1000).Select(index => new { name = "resource-" + index, location = index % 2 == 0 ? "eastus" : "westus" }),
        });
        var schema = AzureQueryTools.Crop(large, "", null, CancellationToken.None);
        Assert.StartsWith("HTTP 200 OK\nThe ", schema);
        Assert.Contains("too large to return. Repeat the request with query", schema);
        Assert.Contains("Coverage: {\"complete\":false}", schema);
        Assert.Contains("\nSchema:\nit: ", schema);
        Assert.True(schema.Length < AzureQueryTools.InlineCharacters);
        Assert.True(EvidenceInspector.Inspect(schema) is { Success: true, Partial: true });
    }

    [Fact]
    public void QueryReturnsOnlyItsResultBesideCoverage()
    {
        var response = "HTTP 200 OK\n" + JsonSerializer.Serialize(new
        {
            complete = true,
            value = new[] { new { name = "a", status = "failed" }, new { name = "b", status = "Succeeded" } },
        });

        var cropped = AzureQueryTools.Crop(response, "value.Where(x => x.status == \"failed\")", null, CancellationToken.None);

        Assert.Equal("HTTP 200 OK\nCoverage: {\"complete\":true}\nQuery result:\n[{\"name\":\"a\",\"status\":\"failed\"}]", cropped);
        Assert.True(EvidenceInspector.Inspect(cropped) is { Success: true, Partial: false });
    }

    [Fact]
    public void AQueryErrorReturnsTheSchemaToCorrectAgainst()
    {
        var cropped = AzureQueryTools.Crop("HTTP 200 OK\n" + Vms, "value.Select(x => x.missing)", null, CancellationToken.None);

        Assert.StartsWith("Error: the query failed: ", cropped);
        Assert.Contains("missing", cropped);
        Assert.Contains("Correct the query against this schema and repeat the request.", cropped);
        Assert.Contains("\nSchema:\nit: ", cropped);
        Assert.DoesNotContain("AnonymousType", cropped);
        Assert.False(EvidenceInspector.Inspect(cropped).Success);
    }

    [Fact]
    public void FailuresAreReturnedVerbatim()
    {
        const string failure = "HTTP 403 Forbidden\n{\"error\":{\"code\":\"AuthorizationFailed\"}}";
        Assert.Same(failure, AzureQueryTools.Crop(failure, "value.Count()", null, CancellationToken.None));
    }
}
