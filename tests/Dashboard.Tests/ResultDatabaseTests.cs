using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.Tests;

public class ResultDatabaseTests
{
    private static long _owner = 9_000_000;
    private static ResultDatabase Fresh() => ResultDatabase.For(Interlocked.Increment(ref _owner), Guid.NewGuid().ToString("N"));

    private const string Vms = "HTTP 200 OK\nCurrent UTC time: 2026-09-25 10:00:00\n{\"value\":[{\"name\":\"vm-a\",\"properties\":{\"cost\":0.1}},{\"name\":\"vm-b\",\"properties\":{\"cost\":0.2}}]}";

    [Fact]
    public void SmallResponseIsReturnedWholeWithItsId()
    {
        var output = Fresh().Present("/subscriptions/x/vms", "GET", Vms, null, CancellationToken.None);

        Assert.StartsWith("HTTP 200 OK\nCurrent UTC time: 2026-09-25 10:00:00\n{\"value\"", output);
        Assert.EndsWith("[Stored as responses.id = 1.]", output);
    }

    [Fact]
    public void SqlWithTheRequestReturnsOnlyItsRowsAndRoundsMoney()
    {
        var output = Fresh().Present("/subscriptions/x/vms", "GET", Vms,
            "SELECT json_extract(v.value,'$.name') AS name FROM responses r, json_each(r.body,'$.value') v WHERE r.id = $id; ", CancellationToken.None);

        Assert.Equal("HTTP 200 OK\nCurrent UTC time: 2026-09-25 10:00:00\n[Stored as responses.id = 1.]\nname\nvm-a\nvm-b\n(2 rows)", output);
        var database = Fresh();
        database.Present("/x", "GET", Vms, null, CancellationToken.None);
        Assert.Equal("total\n0.3\n(1 rows)", database.Query(
            "SELECT round(sum(json_extract(v.value,'$.properties.cost')), 2) AS total FROM responses r, json_each(r.body,'$.value') v WHERE r.id = 1", null, CancellationToken.None));
    }

    [Fact]
    public void LargeResponseReturnsShapeIncludingEveryColumnName()
    {
        var rows = string.Join(',', Enumerable.Range(0, 3000).Select(index => $"[{index}.5,\"2026-09-{index % 28 + 1:00}\",\"Microsoft.Compute\",\"USD\"]"));
        var response = "HTTP 200 OK\n{\"properties\":{\"columns\":[{\"name\":\"Cost\",\"type\":\"Number\"},{\"name\":\"UsageDate\",\"type\":\"String\"},{\"name\":\"ServiceName\",\"type\":\"String\"},{\"name\":\"Currency\",\"type\":\"String\"}],\"rows\":[" + rows + "]}}";

        var output = Fresh().Present("/subscriptions/x/providers/Microsoft.CostManagement/query", "POST", response, null, CancellationToken.None);

        Assert.True(output.Length < 8_000, output.Length.ToString());
        Assert.Contains("too large to return", output);
        foreach (var column in new[] { "Cost", "UsageDate", "ServiceName", "Currency" }) Assert.Contains("\t" + column + "\n", output);
        Assert.Contains("$.properties.rows\tarray\t3000", output);
        Assert.DoesNotContain("$.properties.rows[1]", output);
    }

    [Fact]
    public void ShareOfTotalOverOneReadOfALargeCostResultIsFast()
    {
        // 500 resources x 28 days: a correlated per-row total over this body took over 40 seconds.
        var rows = string.Join(',', Enumerable.Range(0, 14_000).Select(index => $"[{index / 28 + 1},{20260901 + index % 28},\"/subscriptions/x/resourcegroups/rg/providers/microsoft.compute/virtualmachines/vm-{index / 28:000}\",\"USD\"]"));
        var response = "HTTP 200 OK\n{\"properties\":{\"columns\":[{\"name\":\"Cost\"},{\"name\":\"UsageDate\"},{\"name\":\"ResourceId\"},{\"name\":\"Currency\"}],\"rows\":[" + rows + "]}}";
        var started = DateTime.UtcNow;

        var output = Fresh().Present("/subscriptions/x/providers/Microsoft.CostManagement/query", "POST", response,
            "SELECT res, sum(cost) AS total, round(sum(cost) * 100.0 / sum(sum(cost)) OVER (), 2) AS share, rank() OVER (ORDER BY sum(cost) DESC) AS rk, count(*) OVER () AS resources FROM (SELECT json_extract(v.value,'$[2]') AS res, json_extract(v.value,'$[0]') AS cost FROM responses r, json_each(r.body,'$.properties.rows') v WHERE r.id = $id) GROUP BY res ORDER BY total DESC LIMIT 1",
            CancellationToken.None);

        Assert.EndsWith("/virtualmachines/vm-499\t14000\t0.4\t1\t500\n(1 rows)", output);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Contains("sum(x) OVER ()", AzureQueryTools.ToolDescription);
        Assert.Contains("never with a subquery or self-join that reads json_each(r.body) again for each row", AzureQueryTools.ToolDescription);
        Assert.Contains("window functions", ResultDatabase.TimeoutHint);
    }

    [Fact]
    public void LogAnalyticsShapeListsEveryColumnName()
    {
        var rows = string.Join(',', Enumerable.Range(0, 2000).Select(index => $"[\"Perf\",{index}.25,\"2026-09-01\"]"));
        var response = "HTTP 200 OK\n{\"tables\":[{\"name\":\"PrimaryResult\",\"columns\":[{\"name\":\"DataType\",\"type\":\"string\"},{\"name\":\"GB\",\"type\":\"real\"},{\"name\":\"Day\",\"type\":\"datetime\"}],\"rows\":[" + rows + "]}]}";

        var output = Fresh().Present("https://api.loganalytics.io/v1/workspaces/x/query", "POST", response, null, CancellationToken.None);

        foreach (var column in new[] { "DataType", "GB", "Day" }) Assert.Contains("\t" + column + "\n", output);
        Assert.DoesNotContain("$.tables[0].columns[1].type", output);
    }

    [Fact]
    public void TextBodyShapeExplainsSearch()
    {
        var page = "HTTP 200 OK\nFinal URL: https://learn.microsoft.com/x\nContent-Type: text/html\nBytes on wire: 1\nUTC: 2026-09-25T10:00:00Z\n\n" + new string('a', 40_000) + " spot eviction rate";
        var database = Fresh();

        var output = database.Present("https://learn.microsoft.com/x", "GET", page, null, CancellationToken.None);

        Assert.Contains("text, 40019 characters", output);
        Assert.StartsWith("HTTP 200 OK\nFinal URL: ", output);
        Assert.Equal("hit\nspot eviction\n(1 rows)", database.Query("SELECT substr(body, instr(body, 'spot'), 13) AS hit FROM responses WHERE id = 1", null, CancellationToken.None));
    }

    [Theory]
    [InlineData("INSERT INTO responses(url, method, retrieved_utc, body) VALUES ('a','b','c','d')")]
    [InlineData("DELETE FROM responses")]
    [InlineData("UPDATE responses SET body = ''")]
    [InlineData("CREATE TABLE t(x)")]
    [InlineData("DROP TABLE responses")]
    [InlineData("PRAGMA table_info(responses)")]
    [InlineData("SELECT * FROM pragma_table_info('responses')")]
    [InlineData("ATTACH DATABASE 'file.db' AS other")]
    [InlineData("VACUUM INTO 'copy.db'")]
    [InlineData("SELECT load_extension('x')")]
    [InlineData("SELECT 1; DELETE FROM responses")]
    public void ModelSqlCannotWriteAttachOrLeaveTheDatabase(string sql)
    {
        var database = Fresh();
        database.Present("/x", "GET", Vms, null, CancellationToken.None);

        var output = database.Query(sql, null, CancellationToken.None);

        Assert.True(output.StartsWith("Error: SQL failed", StringComparison.Ordinal)
            || output.StartsWith("PARTIAL RESULT: 1 of 2 SQL statements failed", StringComparison.Ordinal) && output.Contains("statement 2 failed: SQLite Error 23: 'not authorized'", StringComparison.Ordinal), output);
        Assert.Equal("n\n1\n(1 rows)", database.Query("SELECT count(*) AS n FROM responses", null, CancellationToken.None));
        Assert.False(File.Exists("copy.db"));
        Assert.False(File.Exists("file.db"));
    }

    [Fact]
    public void RunawayQueryStopsOnCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;

        var output = Fresh().Query("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT count(*) FROM n", null, cancellation.Token);

        Assert.StartsWith("Error: SQL failed", output);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ConversationsAreIsolated()
    {
        var mine = ResultDatabase.For(8_000_001, "conversation");
        mine.Present("/x", "GET", Vms, null, CancellationToken.None);

        Assert.Equal("n\n0\n(1 rows)", ResultDatabase.For(8_000_002, "conversation").Query("SELECT count(*) AS n FROM responses", null, CancellationToken.None));
        Assert.Equal("n\n0\n(1 rows)", ResultDatabase.For(8_000_001, "other").Query("SELECT count(*) AS n FROM responses", null, CancellationToken.None));
        Assert.Same(mine, ResultDatabase.For(8_000_001, "conversation"));
    }

    [Fact]
    public void LargeOutputIsCappedWithRowCount()
    {
        var output = Fresh().Query("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 20000) SELECT x, 'padding padding padding' FROM n", null, CancellationToken.None);

        Assert.Contains("of 20000 rows; aggregate, filter or page", output);
        Assert.True(output.Length < 60_000);
    }

    [Fact]
    public void SqlErrorWithRequestReturnsShapeWithoutFailing()
    {
        var output = Fresh().Present("/x", "GET", Vms, "SELECT nope FROM responses WHERE id = $id", CancellationToken.None);

        Assert.Contains("Error: SQL failed", output);
        Assert.Contains("$.value[0].name\ttext\tnull\tvm-a", output);
    }

    [Fact]
    public void SeveralSelectStatementsReturnOneTableEach()
    {
        var database = Fresh();
        database.Present("/x", "GET", Vms, null, CancellationToken.None);

        var output = database.Query("SELECT count(*) AS n FROM responses; SELECT json_extract(v.value,'$.name') AS name FROM responses r, json_each(r.body,'$.value') v WHERE r.id = 1 ORDER BY name;", null, CancellationToken.None);

        Assert.Equal("n\n1\n(1 rows)\n\nname\nvm-a\nvm-b\n(2 rows)", output);
    }

    [Fact]
    public void AFailedStatementIsReportedBesideTheOtherTables()
    {
        var database = Fresh();
        database.Present("/x", "GET", Vms, null, CancellationToken.None);

        var partial = database.Query("SELECT count(*) AS n FROM responses; SELECT t.value FROM (SELECT 1 AS x) t; SELECT 2 AS two", null, CancellationToken.None);
        var failed = database.Query("SELECT nope FROM responses; SELECT t.value FROM (SELECT 1 AS x) t", null, CancellationToken.None);

        Assert.StartsWith("PARTIAL RESULT: 1 of 3 SQL statements failed; a failed statement is unknown, not empty.", partial);
        Assert.Contains("\n\nn\n1\n(1 rows)\n\nstatement 2 failed: SQLite Error 1: 'no such column: t.value'.\n\ntwo\n2\n(1 rows)", partial);
        Assert.True(ProtectedTool.InspectEvidence(partial) is { Success: true, Partial: true });
        Assert.StartsWith("Error: SQL failed: all 2 statements failed.\n\nstatement 1 failed: SQLite Error 1: 'no such column: nope'.", failed);
        Assert.False(ProtectedTool.InspectEvidence(failed).Success);
    }

    [Theory]
    [InlineData("SELECT 'a;b' AS s; SELECT \"x;y\" FROM (SELECT 2 AS \"x;y\")", "s\na;b\n(1 rows)\n\nx;y\n2\n(1 rows)")]
    [InlineData("-- totals; then names\nSELECT 'it''s' AS s; /* ; */ ;", "s\nit's\n(1 rows)")]
    [InlineData("SELECT [a;b] FROM (SELECT 3 AS [a;b]);;", "a;b\n3\n(1 rows)")]
    public void StatementsSplitOnlyOutsideLiteralsAndComments(string sql, string expected) =>
        Assert.Equal(expected, Fresh().Query(sql, null, CancellationToken.None));

    [Fact]
    public void SqlWithoutAStatementFails() =>
        Assert.Equal("Error: SQL failed: the sql holds no statement.", Fresh().Query(" -- nothing ; ", null, CancellationToken.None));

    [Fact]
    public void SharedShapesAreShownOnceWithinOneCall()
    {
        static string Large(string name) => "HTTP 200 OK\n{\"value\":[" + string.Join(',', Enumerable.Range(0, 900).Select(index =>
            $"{{\"name\":\"{name}-{index}\",\"locations\":[\"eastus\"],\"restrictions\":[]}}")) + "]}";
        var database = Fresh();
        var shapes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        var first = database.Present("/r1", "GET", Large("a"), null, CancellationToken.None, shapes);
        var second = database.Present("/r2", "GET", Large("b"), null, CancellationToken.None, shapes);
        var other = database.Present("/r3", "GET", "HTTP 200 OK\n{\"items\":[" + string.Join(',', Enumerable.Range(0, 3000).Select(index => $"{{\"id\":{index}}}")) + "]}",
            null, CancellationToken.None, shapes);
        var alone = database.Present("/r4", "GET", Large("c"), null, CancellationToken.None);

        Assert.Contains("$.value[0].restrictions\tarray", first);
        Assert.EndsWith("Shape:\nthe same paths and types as responses.id = 1.", second);
        Assert.Contains("$.items[0].id\tinteger", other);
        Assert.Contains("$.value[0].restrictions\tarray", alone);
    }

    [Fact]
    public void FailuresAreReturnedVerbatim()
    {
        const string failure = "HTTP 404 NotFound\n{\"error\":{\"code\":\"ResourceNotFound\"}}";
        Assert.Equal(failure, Fresh().Present("/x", "GET", failure, "SELECT 1", CancellationToken.None));
    }
}
