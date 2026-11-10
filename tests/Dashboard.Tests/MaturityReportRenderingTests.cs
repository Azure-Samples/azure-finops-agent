using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

/// <summary>
/// An assessment report showed capabilities it never checked as 0/5 "Not Started", because a missing score read as 0
/// (session evaluation of 107 conversations, November 2026). A null score renders as not assessed and stays out of the
/// chart; a checked capability that found nothing still scores 0.
/// </summary>
public sealed class MaturityReportRenderingTests
{
    [Fact]
    public void AnUnassessedCapabilityIsNeitherZeroNorCharted()
    {
        using var report = JsonDocument.Parse("""
            {
              "domains": [{ "name": "Understand Cloud Usage & Cost", "capabilities": 2, "avgScore": null, "stage": "Not assessed" }],
              "capabilities": [
                { "domain": "Understand Cloud Usage & Cost", "name": "Data Ingestion", "score": null, "stage": "Not assessed",
                  "evidence": ["Billing-scope exports were not read."], "perSubscription": [{ "name": "sub-a", "score": null }] },
                { "domain": "Understand Cloud Usage & Cost", "name": "Cost Allocation", "score": 0, "stage": "Not Started",
                  "evidence": ["0 of 40 resources carry an owner tag."] },
                { "domain": "Quantify Business Value", "name": "Budgeting", "score": 1, "stage": "Crawl", "evidence": ["1 of 6 subscriptions has a budget."] },
                { "domain": "Quantify Business Value", "name": "Anomaly Management", "score": 2, "stage": "Crawl", "evidence": ["7 anomaly alerts."] }
              ]
            }
            """);

        var html = MaturityReportTools.BuildBody(report.RootElement, null, out var count);

        Assert.Equal(4, count);
        var unassessed = html[html.IndexOf("<h4>Data Ingestion</h4>", StringComparison.Ordinal)..html.IndexOf("<h4>Cost Allocation</h4>", StringComparison.Ordinal)];
        Assert.Contains(">Not assessed</span>", unassessed);
        Assert.Contains("badge-not-assessed", unassessed);
        Assert.Contains("sub-a: not assessed", unassessed);
        Assert.DoesNotContain("/5", unassessed);
        Assert.Contains(">0/5</span>", html[html.IndexOf("<h4>Cost Allocation</h4>", StringComparison.Ordinal)..]);
        Assert.Contains("labels:[\"Cost Allocation\",\"Budgeting\",\"Anomaly Management\"]", html);
        Assert.Contains("Not assessed, so not charted: Data Ingestion.", html);
        Assert.DoesNotContain("0.0/5", html);
    }
}
