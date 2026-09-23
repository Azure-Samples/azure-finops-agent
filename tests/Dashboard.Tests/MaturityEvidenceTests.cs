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

    [Fact]
    public void RenderedScoresPreserveBudgetFreshnessAndAlertInventoryLimits()
    {
        var empty = new { status = 200, data = Array.Empty<object>() };
        var scores = CrawlMaturityTools.BuildScores([new("synthetic", "Synthetic")],
            [new("synthetic", "Synthetic", 200, 1, 100, 25, "USD", 1, 1, null)], empty,
            [new("synthetic", "Synthetic", 200, 0, [], null)],
            [new("synthetic", "Synthetic", 200, 0, [], null)],
            [new("synthetic", "Synthetic", 200, 1, ["Synthetic alert"], null)],
            empty, empty, empty, 25, "USD", new Dictionary<string, double> { ["USD"] = 25 });

        foreach (var id in new[] { "budgets", "visibility" })
        {
            var detail = scores.Single(score => score.Id == id).Detail;
            Assert.Contains("may lag billing", detail);
            Assert.Contains("No source data-as-of timestamp", detail);
            Assert.Contains("not a finalized bill", detail);
        }

        Assert.Contains("1 cost alerts", scores.Single(score => score.Id == "alerts").Detail);
        Assert.Contains("do not establish anomaly-alert configuration", scores.Single(score => score.Id == "alerts").Detail);
    }

    [Fact]
    public void RemediationVerifiesControlCoverageBeforeProposingApprovedChanges()
    {
        var prompt = CrawlMaturityTools.BuildRemediationPrompt(3);
        Assert.Contains("across 3 subscriptions", prompt);
        Assert.Contains("verify daily-export schedules and anomaly-alert configuration", prompt);
        Assert.Contains("confirmed gaps", prompt);
        Assert.Contains("explicit application approval", prompt);
        Assert.Contains("do not delete", prompt);
        Assert.DoesNotContain("configure missing", prompt);
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