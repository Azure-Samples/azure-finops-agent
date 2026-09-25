using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class SavingsLedgerTests
{
    private static SavingsLedgerTools.LedgerEntry Entry(string id, string category, string scope,
        double estimate, string status, int day, double? verified = null)
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day);
        return new(id, created, "Synthetic action", category, scope, estimate, verified, status, created);
    }

    private static SavingsLedgerTools.LedgerEntry[] Entries() =>
    [
        Entry("proposed", "cleanup", "scope-a", 10, "proposed", 1),
        Entry("executed", "cleanup", "scope-a", 20, "executed", 2),
        Entry("dismissed", "cleanup", "scope-a", 30, "dismissed", 3),
        Entry("other", "tagging", "scope-b", 0, "proposed", 4),
        Entry("verified", "cleanup", "scope-a", 15, "verified", 5, 12)
    ];

    [Fact]
    public void TotalsCoverAllMatchesBeforePaging()
    {
        using var result = JsonDocument.Parse(SavingsLedgerTools.FilterLedger(Entries(), limit: "1"));
        var root = result.RootElement;
        Assert.Equal(5, root.GetProperty("sourceEntries").GetInt32());
        Assert.Equal(5, root.GetProperty("matchedEntries").GetInt32());
        Assert.Equal(1, root.GetProperty("returnedEntries").GetInt32());
        Assert.Equal(45, root.GetProperty("totals").GetProperty("estimatedMonthlyUsd").GetDouble());
        Assert.Equal(12, root.GetProperty("totals").GetProperty("verifiedMonthlyUsd").GetDouble());
        Assert.True(root.GetProperty("totalsComplete").GetBoolean());
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.Equal(1, root.GetProperty("nextOffset").GetInt32());
    }

    [Fact]
    public void FiltersMatchCaseInsensitivelyBeforeTotals()
    {
        using var result = JsonDocument.Parse(SavingsLedgerTools.FilterLedger(Entries(),
            status: "EXECUTED", category: "Cleanup", scopeContains: "SCOPE-A"));
        var root = result.RootElement;
        Assert.Equal(1, root.GetProperty("matchedEntries").GetInt32());
        Assert.Equal("executed", root.GetProperty("entries")[0].GetProperty("Id").GetString());
        Assert.Equal(20, root.GetProperty("totals").GetProperty("estimatedMonthlyUsd").GetDouble());
        Assert.True(root.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void ScopeFilterIsLiteralAndNoMatchHasZeroTotals()
    {
        using var result = JsonDocument.Parse(SavingsLedgerTools.FilterLedger(Entries(), scopeContains: ".*"));
        var root = result.RootElement;
        Assert.Equal(5, root.GetProperty("sourceEntries").GetInt32());
        Assert.Equal(0, root.GetProperty("matchedEntries").GetInt32());
        Assert.Empty(root.GetProperty("entries").EnumerateArray());
        Assert.Equal(0, root.GetProperty("totals").GetProperty("estimatedMonthlyUsd").GetDouble());
        Assert.True(root.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void SummaryOnlyOmitsDetailsWithoutChangingTotals()
    {
        using var result = JsonDocument.Parse(SavingsLedgerTools.FilterLedger(Entries(), limit: "0"));
        var root = result.RootElement;
        Assert.Empty(root.GetProperty("entries").EnumerateArray());
        Assert.Equal(45, root.GetProperty("totals").GetProperty("estimatedMonthlyUsd").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nextOffset").ValueKind);
        Assert.True(root.GetProperty("totalsComplete").GetBoolean());
        Assert.False(root.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void DefaultPageIsBoundedAndNextPageDoesNotOverlap()
    {
        var entries = Enumerable.Range(1, 60).Select(index => Entry("entry-" + index, "cleanup", "scope-a", 1, "proposed", index)).ToArray();
        using var first = JsonDocument.Parse(SavingsLedgerTools.FilterLedger(entries));
        using var second = JsonDocument.Parse(SavingsLedgerTools.FilterLedger(entries, offset: "50"));
        Assert.Equal(50, first.RootElement.GetProperty("returnedEntries").GetInt32());
        Assert.Equal(50, first.RootElement.GetProperty("nextOffset").GetInt32());
        Assert.Equal(10, second.RootElement.GetProperty("returnedEntries").GetInt32());
        Assert.Equal(60, second.RootElement.GetProperty("totals").GetProperty("estimatedMonthlyUsd").GetDouble());
        var firstIds = first.RootElement.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("Id").GetString());
        var secondIds = second.RootElement.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("Id").GetString());
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    [Theory]
    [InlineData("invalid", null, "50", "0")]
    [InlineData(null, "invalid", "50", "0")]
    [InlineData(null, null, "201", "0")]
    [InlineData(null, null, "1.5", "0")]
    [InlineData(null, null, "50", "-1")]
    public void InvalidFiltersAndPagingAreRejected(string? status, string? category, string limit, string offset)
        => Assert.StartsWith("Error:", SavingsLedgerTools.FilterLedger(Entries(), status: status, category: category, limit: limit, offset: offset));
}