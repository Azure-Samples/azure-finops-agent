using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class ComputeDiagnosticTests
{
    private static JsonElement Sku(object[] restrictions) => JsonSerializer.SerializeToElement(new
    {
        name = "Standard_Test", family = "testFamily", locations = new[] { "testregion" },
        locationInfo = new[] { new { location = "testregion", zones = new[] { "1", "2" } } },
        capabilities = new[] { new { name = "vCPUs", value = "8" } }, restrictions
    });
    private static JsonElement Usage(string name, int limit, int current = 0) => JsonSerializer.SerializeToElement(new { name = new { value = name }, limit, currentValue = current });

    [Fact]
    public void RestrictionsOverrideSufficientQuota()
    {
        var result = ComputeDiagnosticTools.Evaluate(Sku([new { type = "Location", values = new[] { "testregion" } }]), "testregion", null,
            [Usage("cores", 100), Usage("testFamily", 100)], 1, "standard");
        Assert.Equal("blocked", result.SkuStatus);
        Assert.Equal("sufficient", result.QuotaStatus);
    }

    [Fact]
    public void SpotUsesSpotQuotaInsteadOfStandardFamilyQuota()
    {
        var usages = new[] { Usage("cores", 0), Usage("testFamily", 0), Usage("lowPriorityCores", 32) };
        Assert.Equal("sufficient", ComputeDiagnosticTools.Evaluate(Sku([]), "testregion", null, usages, 2, "spot").QuotaStatus);
        Assert.Equal("insufficient", ComputeDiagnosticTools.Evaluate(Sku([]), "testregion", null, usages, 2, "standard").QuotaStatus);
    }

    [Fact]
    public void ZoneAndMissingQuotaAreNotAssumedAvailable()
    {
        var result = ComputeDiagnosticTools.Evaluate(Sku([new { type = "Zone", restrictionInfo = new { locations = new[] { "testregion" }, zones = new[] { "2" } } }]), "testregion", "2", [], 1, "standard");
        Assert.Equal("blocked", result.SkuStatus);
        Assert.Equal("unknown", result.QuotaStatus);
        Assert.Equal(["1"], result.EligibleZones);
    }
}