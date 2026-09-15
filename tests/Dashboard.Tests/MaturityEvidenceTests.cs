using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class MaturityEvidenceTests
{
    [Fact]
    public void MissingPermissionDoesNotBecomeZeroAndEmptyEstateIsNotApplicable()
    {
        var empty = new { status = 200, data = Array.Empty<object>() };
        var scores = CrawlMaturityTools.BuildScores([new("synthetic", "Synthetic")],
            [new("synthetic", "Synthetic", 403, 0, 0, null, null, 0, 0, "denied")], empty,
            [new("synthetic", "Synthetic", 403, 0, [], "denied")], [], [], empty, empty,
            new { status = 200, data = new[] { new { emptyGroupCount = 100 } } }, null, null, new Dictionary<string, double>());
        Assert.Null(scores.Single(score => score.Id == "budgets").Score);
        Assert.Equal("unknown", scores.Single(score => score.Id == "budgets").Status);
        Assert.Equal("notApplicable", scores.Single(score => score.Id == "tagging").Status);
        Assert.Equal(5, scores.Single(score => score.Id == "waste").Score);
        Assert.Equal("unknown", scores.Single(score => score.Id == "policy").Status);
    }

    [Theory]
    [InlineData("{\"properties\":{\"enabled\":false}}")]
    [InlineData("{\"properties\":{\"schedule\":{\"status\":\"Inactive\"}}}")]
    [InlineData("{\"properties\":{\"status\":\"Disabled\"}}")]
    public void DisabledControlsDoNotCount(string json) => Assert.False(CrawlMaturityTools.IsEnabledControl(JsonSerializer.Deserialize<JsonElement>(json)));

    [Fact]
    public void ReportedUnknownAndNotApplicableScoresAreNull()
    {
        var normalized = ScoreTools.NormalizeScores("[{\"id\":\"test\",\"label\":\"Test\",\"score\":0,\"status\":\"notApplicable\",\"detail\":\"No eligible workloads\"}]");
        using var document = JsonDocument.Parse(normalized!);
        Assert.Equal(JsonValueKind.Null, document.RootElement[0].GetProperty("score").ValueKind);
        Assert.Null(ScoreTools.NormalizeScores("[{\"id\":\"test\",\"label\":\"Test\",\"score\":0,\"detail\":\"No evidence\"}]"));
    }
}