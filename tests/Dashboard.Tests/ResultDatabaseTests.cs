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

        Assert.True(output.StartsWith("Error: SQL failed", StringComparison.Ordinal) || output.StartsWith("1\n1\n", StringComparison.Ordinal), output);
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
    public void FailuresAreReturnedVerbatim()
    {
        const string failure = "HTTP 404 NotFound\n{\"error\":{\"code\":\"ResourceNotFound\"}}";
        Assert.Equal(failure, Fresh().Present("/x", "GET", failure, "SELECT 1", CancellationToken.None));
    }
}
