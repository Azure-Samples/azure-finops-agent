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
    public void ComparingANegatedStringTestWithFalseKeepsTheMatchesAsTheDescriptionWarns()
    {
        const string meters = """{"Items":[{"meterName":"gpt-4o-0806-Inp-glbl Tokens"},{"meterName":"gpt-4o-mini-0718-Inp-glbl Tokens"}]}""";
        Assert.Equal("""["gpt-4o-mini-0718-Inp-glbl Tokens"]""",
            Run(meters, "Items.Where(x => !x.meterName.Contains(\"mini\") == false).Select(x => x.meterName)"));
        Assert.Equal("""["gpt-4o-0806-Inp-glbl Tokens"]""",
            Run(meters, "Items.Where(x => !x.meterName.Contains(\"mini\")).Select(x => x.meterName)"));
        Assert.Contains("!x.meterName.Contains(\"mini\") == false keeps the mini rows", AzureQueryTools.ToolDescription);
    }

    [Fact]
    public void ColumnRowTablesAndTextBecomeQueryableRows()
    {
        const string cost = """{"properties":{"columns":[{"name":"PreTaxCost","type":"Number"},{"name":"ResourceGroup","type":"String"}],"rows":[[1.5,"a"],[2.5,"b"]]}}""";
        Assert.Equal("""["b"]""", Run(cost, "properties.rows.Where(r => r.PreTaxCost > 2).Select(r => r.ResourceGroup)"));
        Assert.Equal("""["beta"]""", Run("alpha\nbeta\n", "lines.Where(x => x.StartsWith(\"b\"))"));
        Assert.Equal("2", Run("""[{"a":1},{"a":2}]""", "Max(x => x.a)"));
    }

    // An observed page search tried IndexOf and an indexed Select, then refetched the page to bisect Skip/Take ranges.
    [Theory]
    [InlineData("lines.IndexOf(\"WS2 plan\")")]
    [InlineData("lines.FindIndex(l => l.Contains(\"WS2\"))")]
    [InlineData("lines.Select((l, i) => new { l, i }).Where(x => x.l.Contains(\"WS2\"))")]
    public void LinePositionSearchesNameTheSingleCallIdiom(string query)
    {
        const string page = "intro\n \nWS2 plan\n2 vCPU\n7 GiB\nend\n";
        var error = Assert.ThrowsAny<ArgumentException>(() => Run(page, query));

        Assert.Contains("lines.TakeWhile(l => !l.Contains(\"WS2\")).Count()", error.Message);
        Assert.Contains("lines.SkipWhile(l => !l.Contains(\"WS2\")).Take(60)", error.Message);
        Assert.Equal("""{"at":2,"section":["WS2 plan","2 vCPU","7 GiB"]}""",
            Run(page, "new { at = lines.TakeWhile(l => !l.Contains(\"WS2\")).Count(), section = lines.SkipWhile(l => !l.Contains(\"WS2\")).Take(3) }"));
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

    // The system prompt's per-1M conversion must work over Foundry token meters that mix 1K and 1M units.
    [Fact]
    public void FoundryTokenRatesNormalizePerMillionAcrossUnits()
    {
        const string foundry = """{"Items":[{"productName":"Azure OpenAI","meterName":"gpt 4.1 Inp glbl Tokens","retailPrice":0.002,"unitOfMeasure":"1K","effectiveStartDate":"2025-04-01T00:00:00Z"},{"productName":"Azure OpenAI GPT6","meterName":"6-sol ShortCo Inp Std Gl 1M Tokens","retailPrice":4,"unitOfMeasure":"1M","effectiveStartDate":"2026-09-01T00:00:00Z"}],"retrievedAtUtc":"2026-01-01T00:00:00Z","pages":1,"complete":true}""";
        Assert.Equal("""[{"meterName":"6-sol ShortCo Inp Std Gl 1M Tokens","per1M":4},{"meterName":"gpt 4.1 Inp glbl Tokens","per1M":2}]""",
            Run(foundry, "Items.OrderByDescending(x => x.effectiveStartDate).Select(x => new { x.meterName, per1M = Math.Round(x.retailPrice * (x.unitOfMeasure == \"1K\" ? 1000 : 1), 4) })"));
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
        var cropped = AzureQueryTools.Crop("HTTP 200 OK\n" + Vms, "value.Select(x => x.Name)", null, CancellationToken.None);

        Assert.StartsWith("Error: the query failed: ", cropped);
        Assert.Contains("'Name'", cropped);
        Assert.Contains("Correct the query against this schema and repeat the request.", cropped);
        Assert.Contains("\nSchema:\nit: ", cropped);
        Assert.DoesNotContain("AnonymousType", cropped);
        Assert.False(EvidenceInspector.Inspect(cropped).Success);
    }

    // Each case is a first-call query that failed in the live evaluations against a response of this shape.
    [Fact]
    public void ReadsAreAsForgivingAsJson()
    {
        // An empty list has no items to type, so any member read of an item parses.
        Assert.Equal("""{"total":0,"exports":[]}""",
            Run("""{"value":[]}""", "new { total = value.Count(), exports = value.Select(e => new { e.name, e.properties.schedule.status }).ToList() }"));
        Assert.Equal("[]", Run("""{"totalRecords":0,"count":0,"data":[],"facets":[],"resultTruncated":"false"}""",
            "data.Select(r => new { r.keyName, r.taggedResources }).OrderByDescending(r => r.taggedResources)"));

        // Reading through a null or absent member is null, not an exception; the result names what was absent.
        const string definitions = """{"value":[{"id":"a","properties":{"displayName":"Require a tag"}},{"id":"b","properties":{"policyType":"Custom"}}]}""";
        Assert.Equal("1", Run(definitions, "value.Where(d => d.properties.displayName.ToLower().Contains(\"tag\") || d.id.ToLower().Contains(\"sku\")).Count()"));
        var (json, note) = JsonQuery.Evaluate(JsonQuery.Parse(definitions), "value.Select(d => new { d.id, d.properties.policyRule.then.effect })", 48 * 1024, CancellationToken.None);
        Assert.Equal("""[{"id":"a","effect":null},{"id":"b","effect":null}]""", json);
        Assert.Equal("Not in this response, so read as null: it.value[].properties.policyRule.", note);
        Assert.Equal("""["prod",null,null]""", Run(Vms, "value.Select(x => x.tags[\"env\"])"));

        // An unknown name is an absent data member, never a type: nothing outside the response is reachable.
        (json, note) = JsonQuery.Evaluate(JsonQuery.Parse(Vms), "AppDomain.CurrentDomain", 48 * 1024, CancellationToken.None);
        Assert.Equal("null", json);
        Assert.Equal("Not in this response, so read as null: it.AppDomain.", note);

        // A keyed object read as a collection is a dictionary of its values.
        const string budgets = """{"value":[{"name":"b","properties":{"notifications":{"Actual_GreaterThan_80_Percent":{"enabled":true,"operator":"GreaterThan","threshold":80},"Forecasted_GreaterThan_100_Percent":{"enabled":false,"threshold":100}}}}]}""";
        Assert.Equal("""[{"name":"b","count":2,"notifications":[{"Key":"Actual_GreaterThan_80_Percent","enabled":true,"operator":"GreaterThan","threshold":80},{"Key":"Forecasted_GreaterThan_100_Percent","enabled":false,"operator":null,"threshold":100}]}]""",
            Run(budgets, "value.Select(b => new { b.name, count = b.properties.notifications.Count(), notifications = b.properties.notifications.Select(n => new { n.Key, n.Value.enabled, n.Value.operator, n.Value.threshold }).ToList() })"));

        // An object over the cap keeps its fields and trims its largest list.
        var large = JsonSerializer.Serialize(new { count = 500, value = Enumerable.Range(0, 500).Select(index => new { name = "policy-" + index, rule = new string('x', 200) }) });
        (json, note) = JsonQuery.Evaluate(JsonQuery.Parse(large), "new { count, value }", 8 * 1024, CancellationToken.None);
        Assert.True(json.Length <= 8 * 1024);
        Assert.StartsWith("{\"count\":500,\"value\":[{\"name\":\"policy-0\"", json);
        Assert.Matches(@"^Result truncated to the 8 KB cap: result\.value shows \d+ of 500 items\.", note);

        // A case mistake is still an error; Math accepts a double? and names the fix only when a null reaches it.
        Assert.Contains("'Name'", Assert.ThrowsAny<Exception>(() => Run(Vms, "value.Select(x => x.Name)")).Message);
        Assert.Equal("[12.5,7.25]", Run(Vms, "value.Where(x => x.cost != null).Select(x => Math.Max(0, x.cost))"));
        Assert.Contains("?? (Math.Max(0, x.a ?? 0))", Assert.ThrowsAny<Exception>(() => Run(Vms, "value.Select(x => Math.Max(0, x.cost))")).Message);
    }

    // The exact first-call queries that failed in live evaluation run 36541681695, against bodies of the logged shapes.
    [Theory]
    [InlineData("""{"totalRecords":0,"count":0,"data":[],"facets":[],"resultTruncated":"false"}""",
        "new { keys = data.Select(r => new { r.keyName, r.taggedResources, r.placeholders }).ToList(), variants = data.Count(), placeholderTotal = data.Sum(r => r.placeholders ?? 0) }",
        """{"keys":[],"variants":0,"placeholderTotal":0}""")]
    [InlineData("""{"value":[{"id":"/b","name":"FDPOAzureBudget","properties":{"amount":100,"timeGrain":"Monthly","timePeriod":{"startDate":"2026-01-01"},"currentSpend":{"amount":12},"notifications":{"Actual_GreaterThan_80_Percent":{"enabled":true,"operator":"GreaterThan","threshold":80,"thresholdType":"Actual"}}}}]}""",
        "new { n = value.Count(), budgets = value.Select(b => new { b.name, b.id, b.properties.amount, b.properties.timeGrain, b.properties.timePeriod, b.properties.currentSpend, b.properties.forecastSpend, notificationCount = b.properties.notifications.Count(), notifications = b.properties.notifications.Select(n => new { n.Key, n.Value.enabled, n.Value.operator, n.Value.threshold, n.Value.thresholdType }).ToList() }).ToList() }",
        """{"n":1,"budgets":[{"name":"FDPOAzureBudget","id":"/b","amount":100,"timeGrain":"Monthly","timePeriod":{"startDate":"2026-01-01"},"currentSpend":{"amount":12},"forecastSpend":null,"notificationCount":1,"notifications":[{"Key":"Actual_GreaterThan_80_Percent","enabled":true,"operator":"GreaterThan","threshold":80,"thresholdType":"Actual"}]}]}""")]
    [InlineData("""{"value":[]}""",
        "new { total = value.Count(), exports = value.Select(e => new { e.name, e.properties.schedule.status, e.properties.schedule.recurrence }).ToList() }",
        """{"total":0,"exports":[]}""")]
    [InlineData("""{"value":[],"nextLink":null}""",
        "new { total = value.Count(), byKind = value.GroupBy(a => a.kind).Select(g => new { kind = g.Key, n = g.Count() }).ToList(), actions = value.Select(a => new { a.name, a.kind, a.properties.status }).ToList() }",
        """{"total":0,"byKind":[],"actions":[]}""")]
    [InlineData("""{"value":[{"id":"/s","properties":{"displayName":"Set","policyType":"Custom","policyDefinitions":[{"policyDefinitionId":"/d"}]}}]}""",
        "new { total = value.Count(), definitions = value.Select(d => new { d.id, d.properties.displayName, d.properties.policyType, d.properties.policyRule, d.properties.policyDefinitions }).ToList() }",
        """{"total":1,"definitions":[{"id":"/s","displayName":"Set","policyType":"Custom","policyRule":null,"policyDefinitions":[{"policyDefinitionId":"/d"}]}]}""")]
    [InlineData("""{"value":[{"id":"/d1","properties":{"displayName":"Require a cost tag","policyRule":{"then":{"effect":"deny"}}}},{"id":"/d2","properties":{"policyType":"Custom"}}]}""",
        "new { total = value.Count(), finops = value.Where(d => d.properties.displayName.ToLower().Contains(\"tag\") || d.properties.displayName.ToLower().Contains(\"cost\") || d.properties.displayName.ToLower().Contains(\"budget\") || d.properties.displayName.ToLower().Contains(\"sku\")).Select(d => new { d.id, d.properties.displayName, d.properties.policyRule.then.effect }).ToList() }",
        """{"total":2,"finops":[{"id":"/d1","displayName":"Require a cost tag","effect":"deny"}]}""")]
    [InlineData("""{"value":[{"skuId":"s1","skuPartNumber":"E5","consumedUnits":2,"capabilityStatus":"Enabled","appliesTo":"User","prepaidUnits":{"enabled":50,"suspended":0,"warning":0}}]}""",
        "new { totalSkus = it.value.Count(), skus = it.value.Select(x => new { x.skuId, x.skuPartNumber, x.capabilityStatus, x.appliesTo, enabled = x.prepaidUnits.enabled, suspended = x.prepaidUnits.suspended, warning = x.prepaidUnits.warning, assigned = x.consumedUnits, unassigned = Math.Max(0, x.prepaidUnits.enabled - x.consumedUnits) }).OrderBy(x => x.skuPartNumber).ToList(), enabledTotal = it.value.Sum(x => x.prepaidUnits.enabled), assignedTotal = it.value.Sum(x => x.consumedUnits), unassignedTotal = it.value.Sum(x => Math.Max(0, x.prepaidUnits.enabled - x.consumedUnits)) }",
        """{"totalSkus":1,"skus":[{"skuId":"s1","skuPartNumber":"E5","capabilityStatus":"Enabled","appliesTo":"User","enabled":50,"suspended":0,"warning":0,"assigned":2,"unassigned":48}],"enabledTotal":50,"assignedTotal":2,"unassignedTotal":48}""")]
    [InlineData("""{"value":[]}""",
        "new { visibleAccounts = it.value.Count(), accounts = it.value.Select(x => new { x.id, x.name, x.properties.displayName, x.properties.agreementType }).ToList() }",
        """{"visibleAccounts":0,"accounts":[]}""")]
    [InlineData("""{"totalRecords":0,"count":0,"data":[],"facets":[],"resultTruncated":"false"}""",
        "new { groups = it.data.Select(x => new { x.type, x.licenseChoice, x.resources }).ToList(), groupCount = it.data.Count(), resourcesTotal = it.data.Sum(x => x.resources ?? 0) }",
        """{"groups":[],"groupCount":0,"resourcesTotal":0}""")]
    // Run 36629196068: the budgets query that had just succeeded on a subscription failed on resource groups without budgets,
    // and a scheduled-actions query failed because reading x.properties.kind turned the sibling x.kind into an object.
    [InlineData("""{"value":[]}""",
        "new { count = value.Count(), budgets = value.Select(b => new { b.id, b.name, b.properties.amount, b.properties.timeGrain, b.properties.timePeriod, b.properties.currentSpend, b.properties.forecastSpend, notifications = b.properties.notifications.Select(p => new { p.Key, p.Value.enabled, p.Value.threshold, p.Value.thresholdType, p.Value.operator }) }) }",
        """{"count":0,"budgets":[]}""")]
    [InlineData("""{"value":[],"nextLink":null}""",
        "new { count = value.Count(), insightAlerts = value.Count(x => x.kind == \"InsightAlert\" || x.properties.kind == \"InsightAlert\"), actions = value.Select(x => new { x.id, x.name, x.kind, alertKind = x.properties.kind, x.properties.status, x.properties.notification }) }",
        """{"count":0,"insightAlerts":0,"actions":[]}""")]
    [InlineData("""{"value":[],"nextLink":null}""",
        "value.Where(x => x.kind == \"InsightAlert\" || x.properties.kind == \"InsightAlert\").Select(x => new { x.name, alertKind = x.properties.kind, total = x.properties.items.Sum(i => i.cost), x.properties.tags.Count })",
        """[]""")]
    // Same run: one query over the custom policy definition and set definition lists failed on the definition list, whose
    // items never carry the set-only policyDefinitions member, and a built-in definition read failed counting that member.
    [InlineData("""{"value":[{"id":"/d1","properties":{"displayName":"Block VM SKU Sizes","policyType":"Custom","policyRule":{"then":{"effect":"deny"}}}}]}""",
        "new { count = value.Count(), definitions = value.Select(x => new { x.id, x.properties.displayName, x.properties.policyType, effect = x.properties.policyRule.then.effect, definitions = x.properties.policyDefinitions.Select(d => new { d.policyDefinitionId, d.parameters }) }) }",
        """{"count":1,"definitions":[{"id":"/d1","displayName":"Block VM SKU Sizes","policyType":"Custom","effect":"deny","definitions":[]}]}""")]
    [InlineData("""{"properties":{"displayName":"Resources should not be created in West Europe","policyType":"BuiltIn","policyRule":{"then":{"effect":"deny"}}},"id":"/providers/Microsoft.Authorization/policyDefinitions/p"}""",
        "new { id, properties.displayName, properties.policyType, effect = properties.policyRule.then.effect, definitionCount = properties.policyDefinitions.Count() }",
        """{"id":"/providers/Microsoft.Authorization/policyDefinitions/p","displayName":"Resources should not be created in West Europe","policyType":"BuiltIn","effect":"deny","definitionCount":0}""")]
    public void LiveEvaluationFirstCallQueriesRun(string body, string query, string expected) =>
        Assert.Equal(expected, Run(body, query));

    [Fact]
    public void ADuplicateProjectedNameIsReportedAsSuch()
    {
        var error = Assert.ThrowsAny<Exception>(() => Run("""{"value":[],"nextLink":null}""",
            "value.Select(x => new { x.id, x.kind, x.properties.kind })"));
        Assert.StartsWith("The identifier 'kind' was defined more than once; name each projected member once", error.Message);
    }

    [Fact]
    public void FailuresAreReturnedVerbatim()
    {
        const string failure = "HTTP 403 Forbidden\n{\"error\":{\"code\":\"AuthorizationFailed\"}}";
        Assert.Same(failure, AzureQueryTools.Crop(failure, "value.Count()", null, CancellationToken.None));
    }

    // Queries that failed in a gpt-6.1-sol live evaluation of Crawl maturity, against bodies of the logged shapes.
    [Theory]
    [InlineData("""{"value":[{"name":"b","properties":{"amount":3750,"notifications":{"actual_budgetAlert_Level1":{"enabled":true,"operator":"GreaterThan","threshold":80,"contactEmails":[],"contactRoles":[],"contactGroups":["/g"],"thresholdType":"Actual"},"forecast_budgetAlert_Level1":{"enabled":false,"operator":"GreaterThan","threshold":100,"contactEmails":[],"contactRoles":[],"contactGroups":[],"thresholdType":"Forecasted"}}}}]}""",
        "new { total = value.Count(), placeholders = value.Count(x => (x.properties.amount ?? 0) >= 1000000), budgets = value.Select(x => new { x.name, enabledNotifications = x.properties.notifications.Count(n => n.Value.enabled == true), notifications = x.properties.notifications.Select(n => new { name = n.Key, n.Value.enabled, n.Value.operator, n.Value.threshold, n.Value.thresholdType, n.Value.contactGroups }) }) }",
        """{"total":1,"placeholders":0,"budgets":[{"name":"b","enabledNotifications":1,"notifications":[{"name":"actual_budgetAlert_Level1","enabled":true,"operator":"GreaterThan","threshold":80,"thresholdType":"Actual","contactGroups":["/g"]},{"name":"forecast_budgetAlert_Level1","enabled":false,"operator":"GreaterThan","threshold":100,"thresholdType":"Forecasted","contactGroups":[]}]}]}""")]
    [InlineData("""{"value":[{"id":"a","properties":{"policyType":"Custom","policyRule":{"then":{"effect":"[parameters('effect')]"}},"parameters":{"effect":{"type":"String","defaultValue":"Audit"}}}},{"id":"b","properties":{"policyType":"Custom","policyRule":{"then":{"effect":"deny"}},"parameters":{"location":{"type":"String"}}}}]}""",
        "value.Select(x => new { x.id, effect = x.properties.policyRule.then.effect, defaultEffect = x.properties.parameters[\"effect\"].defaultValue })",
        """[{"id":"a","effect":"[parameters(\u0027effect\u0027)]","defaultEffect":"Audit"},{"id":"b","effect":"deny","defaultEffect":null}]""")]
    public void KeyedReadsOfObjectsRun(string body, string query, string expected) =>
        Assert.Equal(expected, Run(body, query));

    [Theory]
    [InlineData("value.Count(x => x.enabled == true)", "1")]
    [InlineData("value.Where(x => x.enabled == true).Select(x => x.name)", """["a"]""")]
    [InlineData("value.Count(x => x.enabled != true)", "2")]
    public void NullableBooleansFilterWhenCompared(string query, string expected) =>
        Assert.Equal(expected, Run(Flags, query));

    [Theory]
    [InlineData("value.Count(x => x.enabled)")]
    [InlineData("value.Where(x => x.enabled).Select(x => x.name)")]
    public void BareNullableBooleanPredicatesExplainTheComparison(string query)
    {
        var error = Assert.Throws<ArgumentException>(() => Run(Flags, query));
        Assert.EndsWith("compare them (value.Count(x => x.enabled == true)).", error.Message);
    }

    private const string Flags = """{"value":[{"name":"a","enabled":true},{"name":"b","enabled":false},{"name":"c"}]}""";
}
