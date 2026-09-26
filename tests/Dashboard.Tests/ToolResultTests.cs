using System.Buffers.Text;
using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using AzureFinOps.Dashboard.AI.Tools;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class ToolResultTests
{
    [Fact]
    public void RetainedHandlesAreCompactRandomReferencesNotContentIdentifiers()
    {
        var store = new ToolResultStore();
        var first = store.Retain(101, "synthetic-session", "{\"rows\":[5]}", Source)!;
        var second = store.Retain(101, "synthetic-session", "{\"rows\":[5]}", Source)!;

        Assert.Equal(22, first.Id.Length);
        Assert.Matches("\\A[A-Za-z0-9_-]{22}\\z", first.Id);
        Assert.Equal(16, Base64Url.DecodeFromChars(first.Id).Length);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Text, second.Text);
        Assert.Null(new ToolResultStore().Find(101, "synthetic-session", first.Id));
    }

    [Fact]
    public async Task RegisteredQueryToolEnforcesTheAmbientOwnerAndConversation()
    {
        var entry = ToolResultStore.Default.Retain(101, "synthetic-session", "{\"rows\":[{\"value\":5}]}", Source)!;
        var tool = new ToolResultQueryTools(101).Create().Single();
        var arguments = new AIFunctionArguments { ["resultId"] = entry.Id, ["queryJson"] = "{\"path\":\"$.rows[*]\"}" };
        using (new ToolExecutionContext("synthetic-session", 202, CancellationToken.None))
            Assert.StartsWith("Error:", (await tool.InvokeAsync(arguments))!.ToString());
        using (new ToolExecutionContext("other-session", 101, CancellationToken.None))
            Assert.StartsWith("Error:", (await tool.InvokeAsync(arguments))!.ToString());
        using (new ToolExecutionContext("synthetic-session", 101, CancellationToken.None))
        {
            arguments["resultId"] = entry.Id[..^1];
            Assert.StartsWith("Error:", (await tool.InvokeAsync(arguments))!.ToString());
            var last = entry.Id[^1];
            arguments["resultId"] = entry.Id[..^1]
                + (char.IsUpper(last) ? char.ToLowerInvariant(last) : char.ToUpperInvariant(last));
            Assert.StartsWith("Error:", (await tool.InvokeAsync(arguments))!.ToString());
            arguments["resultId"] = entry.Id;
            using var result = JsonDocument.Parse((await tool.InvokeAsync(arguments))!.ToString()!);
            Assert.Equal(5, result.RootElement.GetProperty("rows")[0].GetProperty("value").GetInt32());
            var evidence = ProtectedTool.InspectEvidence(result.RootElement.GetRawText());
            Assert.False(evidence.Fresh);
            Assert.True(evidence.Partial);
        }
    }

    private static readonly ToolResultStore.Source Source = new("SyntheticRead", DateTimeOffset.UtcNow, true, false, true, "");

    private const string CostTable = """{"properties":{"columns":[{"name":"Cost","type":"Number"},{"name":"Service Name","type":"String"},{"name":"Currency","type":"String"}],"rows":[[1.5,"Storage","USD"],[3,"Compute","USD"],[0.5,"Storage","USD"]]}}""";

    [Fact]
    public void ColumnarRowsAreAddressableByColumnName()
    {
        var entry = ToolResultStore.Default.Retain(101, "columnar-session", CostTable, Source)!;
        using var grouped = JsonDocument.Parse(ToolResultQueryTools.Execute(entry,
            """{"path":"$.properties.rows[*]","groupBy":{"service":"$['Service Name']"},"aggregates":[{"op":"sum","path":"$.Cost","as":"cost"}],"sort":[{"path":"$.cost","direction":"desc"}]}"""));
        var rows = grouped.RootElement.GetProperty("rows");
        Assert.Equal(("Compute", 3m), (rows[0].GetProperty("service").GetString(), rows[0].GetProperty("cost").GetDecimal()));
        Assert.Equal(("Storage", 2.0m), (rows[1].GetProperty("service").GetString(), rows[1].GetProperty("cost").GetDecimal()));

        using var positional = JsonDocument.Parse(ToolResultQueryTools.Execute(entry,
            """{"path":"$.properties.rows[*]","select":{"cost":"$[0]","service":"$[1]"},"where":[{"path":"$.Cost","op":"gt","value":1}]}"""));
        Assert.Equal(["Storage", "Compute"], positional.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("service").GetString()));

        using var totals = JsonDocument.Parse(ToolResultQueryTools.Execute(entry,
            """{"path":"$.properties.rows[*]","aggregates":[{"op":"sum","path":"$.Cost","as":"total"}],"limit":0}"""));
        Assert.Equal(5m, totals.RootElement.GetProperty("totals").GetProperty("total").GetDecimal());

        var schema = JsonSerializer.Serialize(ToolResultStore.Describe(entry));
        Assert.Contains("Service Name", schema);
        Assert.Contains("\"rowCount\":3", schema);
    }

    [Fact]
    public void CommonQueryDialectsAreAcceptedOrRejectedPrecisely()
    {
        var entry = ToolResultStore.Default.Retain(101, "dialect-session", CostTable, Source)!;
        using var aliased = JsonDocument.Parse(ToolResultQueryTools.Execute(entry,
            """{"path":"properties.rows[*]","fields":["Cost","$['Service Name']"],"filter":[{"path":"@.Currency","op":"eq","value":"USD"}],"orderBy":[{"path":"Cost","direction":"desc"}],"top":2}"""));
        var rows = aliased.RootElement.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(3m, rows[0].GetProperty("Cost").GetDecimal());
        Assert.Equal("Compute", rows[0].GetProperty("Service Name").GetString());

        Assert.StartsWith("Error:", ToolResultQueryTools.Execute(entry, """{"path":"$.properties.rows[*]","bogus":1}"""));
        Assert.StartsWith("Error:", ToolResultQueryTools.Execute(entry, """{"where":[],"filter":[]}"""));
    }

    [Fact]
    public void KeysModeListsPropertyNamesForLargeMaps()
    {
        var entry = ToolResultStore.Default.Retain(101, "keys-session", """{"paths":{"/a":{"get":{}},"/b":{"post":{}}},"definitions":{"X":{}}}""", Source)!;
        using var keys = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, """{"mode":"keys","path":"$.paths"}"""));
        Assert.Equal(["/a", "/b"], keys.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetString()));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 50)]
    [InlineData(true, 50)]
    public void EmptyOptionalMapsAndTotalsOnlyQueriesPreserveAllAggregates(bool emptyMaps, int limit)
    {
        var entry = new ToolResultStore().Retain(101, "session",
            """{"data":[{"resourceCount":4,"tagged":2},{"resourceCount":6,"tagged":0}]}""", Source)!;
        var query = new Dictionary<string, object>
        {
            ["path"] = "$.data[*]",
            ["aggregates"] = new[]
            {
                new { op = "sum", path = "$.resourceCount", @as = "resources" },
                new { op = "sum", path = "$.tagged", @as = "tagged" }
            },
            ["limit"] = limit
        };
        if (emptyMaps)
        {
            query["groupBy"] = new Dictionary<string, string>();
            query["select"] = new Dictionary<string, string>();
            query["where"] = Array.Empty<object>();
            query["sort"] = Array.Empty<object>();
        }

        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, JsonSerializer.Serialize(query)));
        var root = response.RootElement;
        Assert.Equal(2, root.GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, root.GetProperty("totalResults").GetInt32());
        Assert.Equal(10m, root.GetProperty("totals").GetProperty("resources").GetDecimal());
        Assert.Equal(2m, root.GetProperty("totals").GetProperty("tagged").GetDecimal());
        Assert.Equal(limit == 0 ? 0 : 1, root.GetProperty("rows").GetArrayLength());
        Assert.True(root.GetProperty("source").GetProperty("Partial").GetBoolean());
        Assert.False(root.GetProperty("source").GetProperty("Fresh").GetBoolean());
    }

    [Fact]
    public void CommonQuerySlipsAreNormalizedInsteadOfFailing()
    {
        var entry = new ToolResultStore().Retain(101, "session",
            """{"data":[{"type":"a","count_":4},{"type":"b","count_":6}]}""", Source)!;
        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry,
            """{"path":"$.data[*]","aggregates":[{"op":"SUM","path":"$.count_"},{"op":"count"}],"sort":[{"path":"$.count_","direction":"DESC"}],"select":{"type":"$.type","count_":"$.count_"},"limit":"10","offset":"0"}"""));
        var root = response.RootElement;
        Assert.Equal(10m, root.GetProperty("totals").GetProperty("sum").GetDecimal());
        Assert.Equal(2, root.GetProperty("totals").GetProperty("count").GetInt32());
        Assert.Equal("b", root.GetProperty("rows")[0].GetProperty("type").GetString());

        Assert.StartsWith("Error: Aggregate names", ToolResultQueryTools.Execute(entry,
            """{"path":"$.data[*]","aggregates":[{"op":"sum","path":"$.count_"},{"op":"sum","path":"$.count_"}]}"""));
        Assert.StartsWith("Error: Query paging", ToolResultQueryTools.Execute(entry, """{"limit":"ten"}"""));
    }

    [Fact]
    public void EmptyOptionalMapsDoNotDiscardRows()
    {
        var entry = new ToolResultStore().Retain(101, "session", """{"rows":[{"count":4},{"count":6}]}""", Source)!;
        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry,
            """{"path":"$.rows[*]","groupBy":{},"select":{},"where":[],"sort":[],"aggregates":[]}"""));
        Assert.Equal(2, response.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(4, response.RootElement.GetProperty("rows")[0].GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("totals").ValueKind);
    }

    [Fact]
    public void ForecastRowsCanBeProjectedSortedAndTotaledWithAnEmptyFilter()
    {
        var entry = new ToolResultStore().Retain(101, "session",
            """{"properties":{"rows":[[4.25,20260902,"USD"],[6.5,20260901,"USD"]]}}""", Source)!;
        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry,
            """{"path":"$.properties.rows[*]","where":[],"groupBy":{},"select":{"date":"$[1]","cost":"$[0]","currency":"$[2]"},"aggregates":[{"op":"sum","path":"$[0]","as":"actualToDate"}],"sort":[{"path":"$.date","direction":"asc"}]}"""));

        var root = response.RootElement;
        Assert.Equal(2, root.GetProperty("totalMatches").GetInt32());
        Assert.Equal(10.75m, root.GetProperty("totals").GetProperty("actualToDate").GetDecimal());
        Assert.Equal(20260901, root.GetProperty("rows")[0].GetProperty("date").GetInt32());
        Assert.Equal(6.5m, root.GetProperty("rows")[0].GetProperty("cost").GetDecimal());
        Assert.Equal("USD", root.GetProperty("rows")[1].GetProperty("currency").GetString());
        Assert.True(root.GetProperty("source").GetProperty("Partial").GetBoolean());
        Assert.False(root.GetProperty("source").GetProperty("Fresh").GetBoolean());
    }

    [Theory]
    [InlineData("$[\"name\"]")]
    [InlineData("$['name']")]
    [InlineData("$.name")]
    public void BracketNotationProjectsNamedFields(string namePath)
    {
        var entry = new ToolResultStore().Retain(101, "session",
            """{"properties":{"columns":[{"name":"Cost","type":"Number"},{"name":"ResourceId","type":"String"}]}}""", Source)!;
        var query = JsonSerializer.Serialize(new { path = "$.properties.columns[*]", select = new { name = namePath }, limit = 10 });

        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, query));
        var rows = response.RootElement.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("ResourceId", rows[1].GetProperty("name").GetString());
    }

    [Fact]
    public void MalformedJsonPathErrorsNameTheInvalidPath()
    {
        var entry = new ToolResultStore().Retain(101, "session", """{"rows":[{"a":1}]}""", Source)!;
        var result = ToolResultQueryTools.Execute(entry, """{"path":"$.rows[*]","select":{"a":"$[a"}}""");
        Assert.StartsWith("Error:", result);
        Assert.Contains("JSONPath", result);
    }

    [Theory]
    [InlineData("""{"where":null}""")]
    [InlineData("""{"where":{}}""")]
    [InlineData("""{"sort":null}""")]
    [InlineData("""{"sort":{}}""")]
    [InlineData("""{"aggregates":null}""")]
    [InlineData("""{"aggregates":{}}""")]
    [InlineData("""{"groupBy":{"currency":"$.currency"},"aggregates":[]}""")]
    public void EmptyArraySupportDoesNotAcceptInvalidOperations(string query)
    {
        var entry = new ToolResultStore().Retain(101, "session", """{"currency":"USD","amount":5}""", Source)!;
        Assert.StartsWith("Error:", ToolResultQueryTools.Execute(entry, query));
    }

    [Theory]
    [InlineData("where", 13)]
    [InlineData("sort", 7)]
    [InlineData("aggregates", 13)]
    public void OptionalOperationArraysKeepTheirUpperBounds(string operation, int count)
    {
        var entry = new ToolResultStore().Retain(101, "session", """{"amount":5}""", Source)!;
        var query = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [operation] = Enumerable.Repeat(new { path = "$.amount" }, count).ToArray()
        });
        Assert.StartsWith("Error:", ToolResultQueryTools.Execute(entry, query));
    }

    [Fact]
    public void GroupedAmountsAreNeverCombinedIntoAnUnrequestedGrandTotal()
    {
        var entry = new ToolResultStore().Retain(101, "session",
            """{"rows":[{"currency":"USD","amount":4},{"currency":"EUR","amount":6}]}""", Source)!;
        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry,
            """{"path":"$.rows[*]","groupBy":{"currency":"$.currency"},"aggregates":[{"op":"sum","path":"$.amount","as":"total"}]}"""));
        Assert.Equal(2, response.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("totals").ValueKind);
    }

    [Theory]
    [InlineData("HTTP 200 OK\n{\"results\":[{\"body\":{\"properties\":{\"rows\":[[1.25,\"a\"],[2.5,\"b\"]]}}}]}")]
    [InlineData("HTTP 200 OK\nCurrent UTC time: 2026-01-15 12:30:00\n{\"results\":[{\"body\":{\"properties\":{\"rows\":[[1.25,\"a\"],[2.5,\"b\"]]}}}]}")]
    [InlineData("{}")]
    public async Task SmallInlineEvidenceStaysValidJsonAndAggregatesExactly(string text)
    {
        var store = ToolResultStore.Default;
        var entry = store.Retain(303, "inline-session", text, Source)!;
        var annotated = ToolResultStore.AnnotateInline(text, entry);
        Assert.StartsWith(entry.Source.Preamble, annotated);
        using var document = JsonDocument.Parse(annotated[entry.Source.Preamble.Length..]);
        Assert.Equal(entry.Id, document.RootElement.GetProperty("_resultQuery").GetProperty("resultId").GetString());
        Assert.Equal(text, entry.Text);
        Assert.Equal(ProtectedTool.InspectEvidence(text), ProtectedTool.InspectEvidence(annotated));
        if (text == "{}") return;
        var tool = new ToolResultQueryTools(303).Create().Single();
        using var context = new ToolExecutionContext("inline-session", 303, CancellationToken.None);
        var query = "{\"path\":\"$.results[*].body.properties.rows[*]\",\"aggregates\":[{\"op\":\"sum\",\"path\":\"$[0]\",\"as\":\"total\"},{\"op\":\"count\",\"as\":\"n\"}]}";
        var output = (await tool.InvokeAsync(new AIFunctionArguments { ["resultId"] = entry.Id, ["queryJson"] = query }))!.ToString()!;
        Assert.Contains("3.75", output);
        Assert.DoesNotContain("Error:", output);
        var withRows = ToolResultQueryTools.Execute(entry, "{\"path\":\"$.results[*].body.properties.rows[*]\",\"select\":{\"cost\":\"$[0]\",\"name\":\"$[1]\"},\"aggregates\":[{\"op\":\"sum\",\"path\":\"$[0]\",\"as\":\"total\"}],\"sort\":[{\"path\":\"$.cost\",\"direction\":\"desc\"}]}");
        using var rowsDocument = JsonDocument.Parse(withRows);
        Assert.Equal(3.75m, rowsDocument.RootElement.GetProperty("totals").GetProperty("total").GetDecimal());
        Assert.Equal("b", rowsDocument.RootElement.GetProperty("rows")[0].GetProperty("name").GetString());
        var unionSchema = ToolResultQueryTools.Execute(entry, "{\"mode\":\"schema\",\"path\":\"$.results[*].body.properties.rows[*]\"}");
        Assert.DoesNotContain("Error:", unionSchema);
        Assert.Contains("\"selectedValues\":2", unionSchema);
    }

    [Fact]
    public void BatchResultsProjectOneRowPerRequestWithFilterExpressions()
    {
        const string text = """{"results":[{"index":0,"body":{"value":[{"name":{"value":"cores"},"limit":100},{"name":{"value":"lowPriorityCores"},"limit":10}]}},{"index":1,"body":{"value":[{"name":{"value":"lowPriorityCores"},"limit":0}]}}]}""";
        var entry = ToolResultStore.Default.Retain(304, "batch-session", text, Source)!;
        var output = ToolResultQueryTools.Execute(entry, """{"path":"$.results[*]","select":{"index":"$.index","spotLimit":"$.body.value[?(@.name.value=='lowPriorityCores')].limit"}}""");
        using var document = JsonDocument.Parse(output);
        var rows = document.RootElement.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(10, rows[0].GetProperty("spotLimit").GetInt32());
        Assert.Equal(0, rows[1].GetProperty("spotLimit").GetInt32());
        using var counted = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, """{"path":"$.results[*]","select":{"index":"$.index","items":"$.body.value.length()"}}"""));
        Assert.Equal(2, counted.RootElement.GetProperty("rows")[0].GetProperty("items").GetInt32());
        Assert.Equal(1, counted.RootElement.GetProperty("rows")[1].GetProperty("items").GetInt32());
        using var names = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, """{"path":"$.results[*]","select":{"names":"$.body.value[*].name.value"}}"""));
        Assert.Equal(["cores", "lowPriorityCores"], names.RootElement.GetProperty("rows")[0].GetProperty("names").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal("lowPriorityCores", names.RootElement.GetProperty("rows")[1].GetProperty("names").GetString());
        using var existential = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, """{"path":"$.results[*]","select":{"index":"$.index"},"where":[{"path":"$.body.value[*].limit","op":"gt","value":1}]}"""));
        Assert.Equal([0], existential.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("index").GetInt32()));
        using var universal = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, """{"path":"$.results[*]","select":{"index":"$.index"},"where":[{"path":"$.body.value[*].limit","op":"ne","value":10}]}"""));
        Assert.Equal([1], universal.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("index").GetInt32()));
        Assert.StartsWith("Error: Group keys must be scalar", ToolResultQueryTools.Execute(entry, """{"path":"$.results[*]","groupBy":{"limit":"$.body.value[*].limit"},"aggregates":[{"op":"count","as":"n"}]}"""));
        Assert.StartsWith("Error: Per-row field paths", ToolResultQueryTools.Execute(entry, """{"path":"$.results[*]","sort":[{"path":"$.body.value[*].limit"}]}"""));
        using var distinct = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, """{"path":"$.results[*].body.value[*]","groupBy":{"name":"$.name.value"}}"""));
        Assert.Equal(2, distinct.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "lowPriorityCores").GetProperty("count").GetInt32());
        using var capped = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, """{"path":"$.results[*]","select":{"index":"$.index"},"limit":250}"""));
        Assert.Equal(2, capped.RootElement.GetProperty("returned").GetInt32());
        Assert.StartsWith("Error: Query paging", ToolResultQueryTools.Execute(entry, """{"offset":200000}"""));
    }

    [Fact]
    public void ResultsAreExactImmutableAndOwnerSessionBound()
    {
        var store = new ToolResultStore();
        const string text = "HTTP 200 OK\n{\"rows\":[{\"region\":\"one\",\"quota\":null},{\"region\":\"two\",\"quota\":10}],\"complete\":false}";
        var entry = store.Retain(101, "session-one", text, Source)!;
        Assert.Equal(text, entry.Text);
        Assert.Equal("HTTP 200 OK\n", entry.Source.Preamble);
        Assert.Same(entry, store.Find(101, "session-one", entry.Id));
        Assert.Null(store.Find(202, "session-one", entry.Id));
        Assert.Null(store.Find(101, "session-two", entry.Id));
        Assert.Null(store.Find(101, null, entry.Id));
        Assert.False(entry.Source.Fresh);
        Assert.True(entry.Source.Partial);
    }

    [Fact]
    public void SchemaDiscoversHeterogeneousRowsWithoutHardcodedFields()
    {
        var entry = new ToolResultStore().Retain(101, "session", "{\"anything\":[{\"nested\":{\"a\":1}},{\"nested\":{\"a\":null,\"late\":true}}]}", Source)!;
        var description = JsonSerializer.SerializeToElement(ToolResultStore.Describe(entry));
        Assert.True(description.GetProperty("storedComplete").GetBoolean());
        Assert.False(description.GetProperty("inlineComplete").GetBoolean());
        var schema = description.GetProperty("schema");
        Assert.True(schema.GetProperty("complete").GetBoolean());
        var fields = schema.GetProperty("fields").EnumerateArray().ToArray();
        var heterogeneous = fields.Single(field => field.GetProperty("path").GetString() == "$[\"anything\"][*][\"nested\"][\"a\"]");
        Assert.Equal(new[] { "null", "number" }, heterogeneous.GetProperty("types").EnumerateArray().Select(type => type.GetString()));
        Assert.Contains(fields, field => field.GetProperty("path").GetString()!.EndsWith("[\"late\"]"));
    }

    [Fact]
    public void ExpiryAndCapacityFailClosedWithoutEvictingUsableResults()
    {
        var clock = new Clock();
        var store = new ToolResultStore(clock, 10);
        var entry = store.Retain(101, "session", "{\"a\":1}", Source)!;
        Assert.Null(store.Retain(101, "session", "{\"b\":2}", Source));
        Assert.NotNull(store.Find(101, "session", entry.Id));
        clock.Now = clock.Now.AddMinutes(31);
        Assert.Null(store.Find(101, "session", entry.Id));
        Assert.NotNull(store.Retain(101, "session", "{\"b\":2}", Source));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("\"scalar\"")]
    public void UnsupportedPayloadsAreNotReplaced(string text) => Assert.Null(new ToolResultStore().Retain(101, "session", text, Source));

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void WhereFiltersPositionalRowsWithoutRegexAndGroupsTheMatches()
    {
        var payload = JsonSerializer.Serialize(new
        {
            rows = new object[]
            {
                new object[] { 10.5m, "/providers/Microsoft.Compute/virtualMachines/a", "USD" },
                new object[] { 4.5m, "/providers/Microsoft.ContainerService/managedClusters/b", "USD" },
                new object[] { 99m, "/providers/Microsoft.Storage/storageAccounts/c", "USD" }
            }
        });
        var entry = new ToolResultStore().Retain(101, "session", payload, Source)!;
        var query = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["path"] = "$.rows[*]",
            ["where"] = new[] { new { path = "$[1]", op = "containsAny", value = new[] { "virtualMachines", "managedClusters" } } },
            ["select"] = new Dictionary<string, string> { ["cost"] = "$[0]" },
            ["groupBy"] = new Dictionary<string, string> { ["currency"] = "$[2]" },
            ["aggregates"] = new[] { new Dictionary<string, string> { ["op"] = "sum", ["path"] = "$[0]", ["as"] = "compute" } }
        });
        using var document = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, query));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("totalMatches").GetInt32());
        Assert.Equal(15m, root.GetProperty("rows")[0].GetProperty("compute").GetDecimal());
        Assert.Contains("select was not applied", root.GetProperty("note").GetString());
        Assert.StartsWith("Error:", ToolResultQueryTools.Execute(entry, """{"path":"$.rows[*]","where":[{"path":"$[1]","op":"matches","value":".*"}]}"""));
    }

    [Fact]
    public void DynamicFilterProjectionAndPagingKeepSourceCoverage()
    {
        var entry = new ToolResultStore().Retain(101, "session", "{\"rows\":[{\"region\":\"one\",\"data\":{\"quota\":3}},{\"region\":\"two\",\"data\":{\"quota\":8}},{\"region\":\"three\",\"data\":{\"quota\":0}}]}", Source)!;
        var result = ToolResultQueryTools.Execute(entry, """{"path":"$.rows[?(@.data.quota > 0)]","select":{"location":"$.region","free":"$.data.quota"},"sort":[{"path":"$.free","direction":"desc"}],"limit":1}""");
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("totalMatches").GetInt32());
        Assert.Equal(2, root.GetProperty("totalResults").GetInt32());
        Assert.Equal("two", root.GetProperty("rows")[0].GetProperty("location").GetString());
        Assert.Equal(1, root.GetProperty("nextOffset").GetInt32());
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("source").GetProperty("Fresh").GetBoolean());
    }

    [Fact]
    public void AggregatesCountAllMatchesBeforePagingAndPreserveNulls()
    {
        var entry = new ToolResultStore().Retain(101, "session", "{\"rows\":[{\"group\":null,\"cost\":2},{\"group\":null,\"cost\":3},{\"group\":\"unknown\",\"cost\":\"not numeric\"}]}", Source)!;
        var result = ToolResultQueryTools.Execute(entry, JsonSerializer.Serialize(new
        {
            path = "$.rows[*]",
            groupBy = new { group = "$.group" },
            aggregates = new[] { new { op = "count", path = "$", @as = "count" }, new { op = "sum", path = "$.cost", @as = "total" } },
            sort = new[] { new { path = "$.total", direction = "desc" } },
            limit = 1
        }));
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("totalMatches").GetInt32());
        Assert.Equal(2, root.GetProperty("totalResults").GetInt32());
        Assert.Equal(5m, root.GetProperty("rows")[0].GetProperty("total").GetDecimal());
        Assert.Equal(2, root.GetProperty("rows")[0].GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rows")[0].GetProperty("group").ValueKind);
        Assert.Equal(1, root.GetProperty("invalidNumeric").GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("{\"file\":\"/etc/passwd\"}")]
    [InlineData("{\"owner\":202}")]
    [InlineData("{\"path\":\"$\",\"path\":\"$.rows\"}")]
    [InlineData("{\"offset\":100001}")]
    [InlineData("{\"mode\":\"execute\"}")]
    [InlineData("{\"path\":\"$[\"}")]
    [InlineData("{\"path\":\"$..*\"}")]
    [InlineData("{\"path\":\"$.rows[?(@.value =~ /a+/)]\"}")]
    public void InvalidAndHostOwnedParametersAreRejected(string query)
    {
        var entry = new ToolResultStore().Retain(101, "session", "{\"rows\":[]}", Source)!;
        Assert.StartsWith("Error:", ToolResultQueryTools.Execute(entry, query));
    }

    [Fact]
    public void LargeSelectionRequestsProjectionInsteadOfDroppingDataSilently()
    {
        var entry = new ToolResultStore().Retain(101, "session", JsonSerializer.Serialize(new { rows = new[] { new { content = new string('x', 20000) } } }), Source)!;
        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, "{\"path\":\"$.rows[*]\"}"));
        Assert.True(response.RootElement.GetProperty("requiresProjection").GetBoolean());
        Assert.False(response.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(1, response.RootElement.GetProperty("totalMatches").GetInt32());
        Assert.ThrowsAny<OperationCanceledException>(() => ToolResultQueryTools.Execute(entry, "{}", new CancellationToken(true)));
    }

    [Fact]
    public void ProjectionPreservesDatesPrecisionAndOriginalCoverage()
    {
        var entry = new ToolResultStore().Retain(101, "session", "{\"rows\":[{\"timestamp\":\"2026-01-01T00:00:00+01:00\",\"amount\":0.1234567890123456789012345678}]}", Source)!;
        var query = JsonSerializer.Serialize(new { path = "$.rows[*]", select = new { date = "$.timestamp", cost = "$.amount" } });
        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, query));
        var root = response.RootElement;
        Assert.Equal("2026-01-01T00:00:00+01:00", root.GetProperty("rows")[0].GetProperty("date").GetString());
        Assert.Equal(0.1234567890123456789012345678m, root.GetProperty("rows")[0].GetProperty("cost").GetDecimal());
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.True(root.GetProperty("source").GetProperty("Partial").GetBoolean());
        Assert.False(root.GetProperty("source").GetProperty("Fresh").GetBoolean());
        Assert.Equal(entry.Sha256, root.GetProperty("sha256").GetString());
    }

    [Fact]
    public void QueryToolSchemaAllowsOmittedQueryAndDefaultsToRoot()
    {
        var tool = new ToolResultQueryTools(101).Create().Single();
        var required = tool.JsonSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("resultId", required);
        Assert.DoesNotContain("queryJson", required);
        var entry = new ToolResultStore().Retain(101, "session", "{\"arbitrary\":1}", Source)!;
        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, "{}"));
        Assert.Equal(1, response.RootElement.GetProperty("rows")[0].GetProperty("arbitrary").GetInt32());
    }

    [Fact]
    public void DynamicViewsPreserveUnrecognizedStatusesAndPartialBatches()
    {
        var entry = new ToolResultStore().Retain(101, "session", "{\"batches\":[{\"complete\":false,\"scores\":[{\"region\":\"one\",\"score\":\"DataNotFoundOrStale\"}]}]}", Source)!;
        using var response = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, "{\"path\":\"$.batches[*]\"}"));
        var batch = response.RootElement.GetProperty("rows")[0];
        Assert.False(batch.GetProperty("complete").GetBoolean());
        Assert.Equal("DataNotFoundOrStale", batch.GetProperty("scores")[0].GetProperty("score").GetString());
        Assert.True(response.RootElement.GetProperty("source").GetProperty("Partial").GetBoolean());
    }

    [Fact]
    public void ProjectionCannotAmplifyAnOversizedField()
    {
        var entry = new ToolResultStore().Retain(101, "session", JsonSerializer.Serialize(new { content = new string('x', 20000) }), Source)!;
        var output = ToolResultQueryTools.Execute(entry, "{\"select\":{\"copy\":\"$.content\"}}");
        Assert.DoesNotContain(new string('x', 1000), output);
        using var document = JsonDocument.Parse(output);
        var placeholder = document.RootElement.GetProperty("rows")[0].GetProperty("copy");
        Assert.Equal(20002, placeholder.GetProperty("characters").GetInt32());
        Assert.Equal("string", placeholder.GetProperty("type").GetString());
    }
}