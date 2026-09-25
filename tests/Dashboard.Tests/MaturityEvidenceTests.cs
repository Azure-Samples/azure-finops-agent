using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class MaturityEvidenceTests
{
    [Fact]
    public void AdvisorReadFiltersCostRecommendationsAtTheSource()
    {
        var path = CrawlMaturityTools.AdvisorCostPath("synthetic");
        var uri = new Uri("https://management.azure.com" + path);
        Assert.Equal("/subscriptions/synthetic/providers/Microsoft.Advisor/recommendations", uri.AbsolutePath);
        Assert.Contains("$filter=Category eq 'Cost'", Uri.UnescapeDataString(uri.Query));
        Assert.Contains("$top=100", uri.Query);
        Assert.Contains("api-version=2025-01-01", uri.Query);
    }

    [Fact]
    public void AdvisorRanksAnnualEstimatesWithinCurrencyWithoutAddingAlternatives()
    {
        var evidence = AdvisorEvidence(
            Recommendation("small", "120", "USD", "999"),
            Recommendation("large", 240m, "USD", "20"),
            Recommendation("different-currency", "10000", "EUR", "833.33"));
        Assert.True(evidence.GetProperty("complete").GetBoolean());
        Assert.True(evidence.GetProperty("rankingComplete").GetBoolean());
        Assert.Equal(3, evidence.GetProperty("recommendationCount").GetInt32());
        var rankings = evidence.GetProperty("rankings").EnumerateArray().ToArray();
        Assert.Equal(2, rankings.Length);
        var usd = rankings.Single(group => group.GetProperty("currency").GetString() == "USD");
        Assert.Equal("year", usd.GetProperty("period").GetString());
        Assert.Equal(2, usd.GetProperty("opportunityCount").GetInt32());
        var first = usd.GetProperty("opportunities")[0].GetProperty("best");
        Assert.Equal("large", first.GetProperty("id").GetString());
        Assert.Equal(240m, first.GetProperty("annualSavingsAmount").GetDecimal());
        Assert.Equal("20", first.GetProperty("extendedProperties").GetProperty("savingsAmount").GetString());
        Assert.Equal("P3Y", first.GetProperty("extendedProperties").GetProperty("term").GetString());
        Assert.Equal("2026-08-21T10:00:00Z", first.GetProperty("lastUpdated").GetString());
        Assert.False(evidence.TryGetProperty("totalSavings", out _));
        Assert.Contains("do not sum", evidence.GetProperty("limitations").GetString());
        Assert.Contains("commitment utilization", evidence.GetProperty("limitations").GetString());
    }

    [Fact]
    public void AdvisorRanksAllScopesBeforeLimitingDetailsAndRetainsCounts()
    {
        var firstScope = Enumerable.Range(1, 9)
            .Select(value => Recommendation($"candidate-{value}", value, "USD")).ToArray();
        var projection = CrawlMaturityTools.BuildAdvisorSavings(
        [
            (new("first", "First synthetic scope"), AdvisorResponse(firstScope)),
            (new("second", "Second synthetic scope"), AdvisorResponse(Recommendation("largest", 1000, "USD")))
        ]);
        var evidence = JsonSerializer.SerializeToElement(projection);
        Assert.Equal(2, evidence.GetProperty("requestedSubscriptionCount").GetInt32());
        Assert.Equal(2, evidence.GetProperty("successfulSubscriptionCount").GetInt32());
        Assert.Equal(10, evidence.GetProperty("recommendationCount").GetInt32());
        Assert.True(evidence.GetProperty("rankingComplete").GetBoolean());
        Assert.False(evidence.GetProperty("detailsComplete").GetBoolean());
        var group = evidence.GetProperty("rankings")[0];
        Assert.Equal(10, group.GetProperty("recommendationCount").GetInt32());
        var opportunities = group.GetProperty("opportunities");
        Assert.Equal(5, opportunities.GetArrayLength());
        Assert.Equal("largest", opportunities[0].GetProperty("best").GetProperty("id").GetString());
        Assert.Equal("second", opportunities[0].GetProperty("best").GetProperty("subscriptionId").GetString());
        Assert.Equal(6m, opportunities[4].GetProperty("bestAnnualSavingsAmount").GetDecimal());
    }

    [Fact]
    public void AdvisorTermAndLookbackAlternativesFormOneOpportunity()
    {
        static object Reservation(string id, decimal annual, string term, string lookback, string sku = "P0v3") => new
        {
            id,
            properties = new
            {
                category = "Cost",
                recommendationTypeId = "reservation-type",
                impactedValue = "synthetic-subscription",
                lastUpdated = "2026-09-24T05:51:15Z",
                extendedProperties = new
                {
                    annualSavingsAmount = annual.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    savingsCurrency = "USD",
                    term,
                    lookbackPeriod = lookback,
                    displayQty = "1",
                    displaySKU = sku,
                    location = "swedencentral",
                    scope = "Single"
                }
            }
        };

        var evidence = AdvisorEvidence(
            Reservation("p1y-60", 266, "P1Y", "60"),
            Reservation("p3y-7", 429, "P3Y", "7"),
            Reservation("p3y-30", 424, "P3Y", "30"),
            Reservation("p1y-7", 271, "P1Y", "7"),
            Reservation("other-sku", 100, "P3Y", "7", "P1v3"),
            Recommendation("rightsize", "300", "USD"));
        var usd = evidence.GetProperty("rankings")[0];
        Assert.Equal(6, usd.GetProperty("recommendationCount").GetInt32());
        Assert.Equal(3, usd.GetProperty("opportunityCount").GetInt32());
        var top = usd.GetProperty("opportunities")[0];
        Assert.Equal(1, top.GetProperty("rank").GetInt32());
        Assert.Equal(429m, top.GetProperty("bestAnnualSavingsAmount").GetDecimal());
        Assert.Equal(266m, top.GetProperty("lowestAnnualSavingsAmount").GetDecimal());
        Assert.Equal(4, top.GetProperty("alternativeCount").GetInt32());
        Assert.True(top.GetProperty("alternativesAreMutuallyExclusive").GetBoolean());
        Assert.Equal("p3y-7", top.GetProperty("best").GetProperty("id").GetString());
        var alternatives = top.GetProperty("alternatives").EnumerateArray().ToArray();
        Assert.Equal(["P3Y", "P3Y", "P1Y", "P1Y"], alternatives.Select(a => a.GetProperty("term").GetString()));
        Assert.Equal("7", alternatives[0].GetProperty("lookbackPeriodDays").GetString());
        Assert.Equal("rightsize", usd.GetProperty("opportunities")[1].GetProperty("best").GetProperty("id").GetString());
        Assert.False(usd.GetProperty("opportunities")[1].GetProperty("alternativesAreMutuallyExclusive").GetBoolean());
        Assert.Equal("other-sku", usd.GetProperty("opportunities")[2].GetProperty("best").GetProperty("id").GetString());
        Assert.True(evidence.GetProperty("detailsComplete").GetBoolean());
        var savingsTable = evidence.GetProperty("answerTable").GetString()!;
        Assert.Contains("| 1 | Advisor cost recommendation (P0v3, swedencentral, qty 1) | Synthetic scope | USD 266–429 | term P3Y, 7-day lookback | 4 | 2026-09-24 |", savingsTable);
        Assert.Contains("| 2 |", savingsTable);
    }

    [Fact]
    public void AdvisorImpactReportGroupsOpportunitiesAndListsEmptyImpactLevels()
    {
        static object Reservation(string id, decimal annual, string term, string lookback) => new
        {
            id,
            properties = new
            {
                category = "Cost",
                impact = "High",
                recommendationTypeId = "reservation-type",
                impactedValue = "synthetic-subscription",
                lastUpdated = "2026-09-24T05:51:15Z",
                shortDescription = new { solution = "Buy reserved instance" },
                extendedProperties = new
                {
                    annualSavingsAmount = annual.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    savingsCurrency = "USD",
                    term,
                    lookbackPeriod = lookback,
                    displayQty = "1",
                    displaySKU = "P0v3",
                    location = "swedencentral",
                    scope = "Single"
                }
            }
        };

        var report = JsonSerializer.SerializeToElement(CrawlMaturityTools.BuildAdvisorImpactReport(
            [(new("synthetic", "Synthetic scope"), AdvisorResponse(
                Reservation("p1y-60", 266, "P1Y", "60"),
                Reservation("p3y-7", 429, "P3Y", "7"),
                Recommendation("rightsize", "1200", "USD")))]));

        Assert.True(report.GetProperty("complete").GetBoolean());
        Assert.Equal(2, report.GetProperty("opportunityCount").GetInt32());
        Assert.Equal(3, report.GetProperty("recommendationCount").GetInt32());
        var groups = report.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(["High", "Medium", "Low"], groups.Select(g => g.GetProperty("impact").GetString()));
        Assert.Equal(2, groups[0].GetProperty("opportunityCount").GetInt32());
        Assert.Equal(0, groups[1].GetProperty("opportunityCount").GetInt32());
        var reservation = groups[0].GetProperty("opportunities")[1];
        Assert.Equal(429m, reservation.GetProperty("bestAnnualSavingsAmount").GetDecimal());
        Assert.Equal(266m, reservation.GetProperty("lowestAnnualSavingsAmount").GetDecimal());
        Assert.Equal(2, reservation.GetProperty("alternativeCount").GetInt32());

        var table = report.GetProperty("answerTable").GetString()!;
        Assert.Contains("| High | Buy reserved instance (P0v3, swedencentral, qty 1) | Synthetic scope | USD 266–429 | term P3Y, 7-day lookback | 2 | 2026-09-24 |", table);
        Assert.Contains("| Medium | No cost recommendations returned |", table);
        Assert.Contains("| Low | No cost recommendations returned |", table);
        Assert.StartsWith("Azure Advisor returned 2 cost opportunities (2 high, 0 medium, 0 low) across 1 subscription; the largest single estimate is USD 1,200/year", report.GetProperty("headline").GetString());
    }

    [Fact]
    public void AdvisorImpactReportMarksUnreadableSubscriptionAsPartial()
    {
        var report = JsonSerializer.SerializeToElement(CrawlMaturityTools.BuildAdvisorImpactReport(
        [
            (new("readable", "Readable"), AdvisorResponse(Recommendation("known", "120", "USD"))),
            (new("denied", "Denied"), "HTTP 403 Forbidden\n{}")
        ]));
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("opportunityCount").ValueKind);
        Assert.Contains("Coverage is partial", report.GetProperty("headline").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("-1")]
    [InlineData("1,200")]
    [InlineData("3,14")]
    public void UnknownAnnualSavingsAreNotRankedAsZero(string? annual)
    {
        var evidence = AdvisorEvidence(Recommendation("unknown", annual, "USD", "10"));
        Assert.True(evidence.GetProperty("complete").GetBoolean());
        Assert.False(evidence.GetProperty("rankingComplete").GetBoolean());
        Assert.Empty(evidence.GetProperty("rankings").EnumerateArray());
        Assert.Equal(1, evidence.GetProperty("unrankedRecommendationCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("unrankedRecommendations")[0]
            .GetProperty("annualSavingsAmount").ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void AnnualSavingsWithoutCurrencyRemainUnranked(string? currency)
    {
        var evidence = AdvisorEvidence(Recommendation("no-currency", "120", currency));
        Assert.False(evidence.GetProperty("rankingComplete").GetBoolean());
        Assert.Empty(evidence.GetProperty("rankings").EnumerateArray());
        Assert.Equal(1, evidence.GetProperty("unrankedRecommendationCount").GetInt32());
    }

    [Fact]
    public void EmptyAdvisorInventoryHasNoInventedCurrencyOrMonetaryTotal()
    {
        var evidence = AdvisorEvidence();
        Assert.True(evidence.GetProperty("complete").GetBoolean());
        Assert.Equal(0, evidence.GetProperty("recommendationCount").GetInt32());
        Assert.Empty(evidence.GetProperty("rankings").EnumerateArray());
        Assert.False(evidence.TryGetProperty("currency", out _));
        Assert.False(evidence.TryGetProperty("totalSavings", out _));
    }

    [Theory]
    [InlineData("HTTP 403 Forbidden\n/private-synthetic-path", 403)]
    [InlineData("HTTP 206 PartialContent\npage limit", 206)]
    [InlineData("HTTP 200 OK\n{", 0)]
    [InlineData("HTTP 200 OK\nnull", 0)]
    [InlineData("HTTP 200 OK\n[]", 0)]
    [InlineData("HTTP 200 OK\n{}", 0)]
    [InlineData("HTTP 200 OK\n{\"value\":[1]}", 0)]
    [InlineData("HTTP 200 OK\n{\"value\":[{\"properties\":{\"category\":\"Security\"}}]}", 0)]
    public void IncompleteAdvisorSourcesDoNotBecomeCompleteZeroResults(string response, int status)
    {
        var projection = CrawlMaturityTools.BuildAdvisorSavings(
        [
            (new("readable", "Readable synthetic scope"), AdvisorResponse(Recommendation("known", "120", "USD"))),
            (new("unavailable", "Unavailable synthetic scope"), response)
        ]);
        var evidence = JsonSerializer.SerializeToElement(projection);
        Assert.False(evidence.GetProperty("complete").GetBoolean());
        Assert.False(evidence.GetProperty("rankingComplete").GetBoolean());
        Assert.False(evidence.GetProperty("detailsComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("recommendationCount").ValueKind);
        Assert.Equal(1, evidence.GetProperty("observedRecommendationCount").GetInt32());
        var failed = evidence.GetProperty("sources")[1];
        Assert.Equal(status, failed.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("recommendationCount").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(failed.GetProperty("error").GetString()));
        Assert.DoesNotContain("/private-synthetic-path", evidence.GetRawText());
    }

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

    private static JsonElement AdvisorEvidence(params object[] recommendations) =>
        JsonSerializer.SerializeToElement(CrawlMaturityTools.BuildAdvisorSavings(
            [(new("synthetic", "Synthetic scope"), AdvisorResponse(recommendations))]));

    private static string AdvisorResponse(params object[] recommendations) =>
        "HTTP 200 OK\n" + JsonSerializer.Serialize(new { value = recommendations });

    private static object Recommendation(string id, object? annual, string? currency, string savingsAmount = "10") => new
    {
        id,
        properties = new
        {
            category = "Cost",
            impact = "High",
            lastUpdated = "2026-08-21T10:00:00Z",
            resourceMetadata = new { resourceId = "/subscriptions/synthetic/resourceGroups/synthetic/providers/Microsoft.Compute/virtualMachines/synthetic" },
            shortDescription = new { solution = "Review a reservation estimate" },
            extendedProperties = new
            {
                annualSavingsAmount = annual,
                savingsAmount,
                savingsCurrency = currency,
                term = "P3Y",
                qty = "2",
                sku = "SyntheticSku"
            }
        }
    };
}