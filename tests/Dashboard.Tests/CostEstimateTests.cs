using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class CostEstimateTests
{
    [Theory]
    [InlineData("1,5")]
    [InlineData("1,000")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    public void AmbiguousOrNonFiniteNumbersAreRejected(string value) => Assert.False(CostEstimateTools.TryNum(value, out _));

    [Theory]
    [InlineData("[{\"label\":\"model\"}]")]
    [InlineData("[{\"label\":\"model\",\"inputPricePer1M\":-1,\"outputPricePer1M\":2}]")]
    [InlineData("[{\"label\":\"model\",\"inputPricePer1M\":\"NaN\",\"outputPricePer1M\":2}]")]
    public void MissingOrInvalidRatesNeverBecomeZero(string models) =>
        Assert.StartsWith("Error:", CostEstimateTools.EstimateTokenCost(models, "1500", "500", "8000"));

    [Fact]
    public void ComponentsReconcileAndEvidenceIsPreserved()
    {
        var result = CostEstimateTools.EstimateTokenCost("[{\"label\":\"model\",\"inputPricePer1M\":0.2,\"outputPricePer1M\":1.25,\"source\":\"synthetic quote\",\"deploymentTier\":\"Global Standard\"}]", "1500", "500", "8000");
        using var document = JsonDocument.Parse(result);
        var model = document.RootElement.GetProperty("models")[0];
        Assert.Equal(7.4m, model.GetProperty("totalMonthlyCost").GetDecimal());
        Assert.Equal(model.GetProperty("totalMonthlyCost").GetDecimal(), model.GetProperty("inputCost").GetDecimal() + model.GetProperty("outputCost").GetDecimal());
        Assert.Equal("synthetic quote", model.GetProperty("evidence").GetProperty("source").GetString());
        Assert.False(document.RootElement.GetProperty("isMeasuredBill").GetBoolean());
    }

    [Fact]
    public void CacheTokensRequireCacheRate() => Assert.StartsWith("Error:", CostEstimateTools.EstimateTokenCost(
        "[{\"label\":\"model\",\"inputPricePer1M\":1,\"outputPricePer1M\":2}]", "100", "50", "10", "25"));
}