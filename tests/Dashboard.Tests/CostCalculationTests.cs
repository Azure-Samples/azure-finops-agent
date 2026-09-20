using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class CostCalculationTests
{
    [Fact]
    public void LinesInheritTheExplicitSharedCurrencyWithoutRedundantArguments()
    {
        var result = CostCalculationTools.CalculateCost(
            """[{"label":"Units","quantity":5,"unitPrice":5,"unit":"unit"}]""", "USD", "month");
        using var document = JsonDocument.Parse(result);
        Assert.Equal(25m, document.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal("USD", document.RootElement.GetProperty("lines")[0].GetProperty("currency").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"EUR\"")]
    [InlineData("123")]
    public void AnExplicitInvalidOrDifferentLineCurrencyIsNotDefaulted(string currencyJson)
    {
        var line = $$"""[{"label":"Units","quantity":5,"unitPrice":5,"unit":"unit","currency":{{currencyJson}}}]""";
        Assert.StartsWith("Error: An explicit line currency", CostCalculationTools.CalculateCost(line, "USD", "month"));
    }

    [Fact]
    public void BackupEstimateReconcilesQuantityRatesAndTax()
    {
        var result = CostCalculationTools.CalculateCost(
            """[{"label":"Synthetic storage","quantity":1400,"unitPrice":0.07,"unit":"GB-month","currency":"CAD"},{"label":"Synthetic protected instance","quantity":1,"unitPrice":10.25,"unit":"instance-month","currency":"CAD"}]""",
            "CAD", "month", taxPercent: "15");
        using var document = JsonDocument.Parse(result);
        var body = document.RootElement;
        Assert.Equal(124.49m, body.GetProperty("total").GetDecimal());
        Assert.Equal(1493.88m, body.GetProperty("annualizedTotal").GetDecimal());
        Assert.Equal(body.GetProperty("total").GetDecimal(),
            body.GetProperty("subtotal").GetDecimal() - body.GetProperty("discountAmount").GetDecimal() + body.GetProperty("taxAmount").GetDecimal());
        Assert.False(body.GetProperty("isMeasuredBill").GetBoolean());
    }

    [Fact]
    public void RunRateUsesTheRequestedNumberOfDays()
    {
        var result = CostCalculationTools.CalculateCost(
            """[{"label":"Synthetic run-rate","quantity":710.25,"unitPrice":1,"unitsPerRate":17,"multiplier":30,"unit":"observed spend","currency":"USD"}]""",
            "USD", "30-day run-rate");
        using var document = JsonDocument.Parse(result);
        Assert.Equal(1253.38m, document.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("annualizedTotal").ValueKind);
    }

    [Theory]
    [InlineData("""[{"label":"Missing rate","quantity":1,"unit":"GB-month","currency":"USD"}]""")]
    [InlineData("""[{"label":"Wrong currency","quantity":1,"unitPrice":2,"unit":"GB-month","currency":"EUR"}]""")]
    [InlineData("""[{"label":"Ambiguous quantity","quantity":"1,460","unitPrice":2,"unit":"GB-month","currency":"USD"}]""")]
    [InlineData("""[{"label":"Invalid divisor","quantity":1,"unitPrice":2,"unitsPerRate":0,"unit":"GB-month","currency":"USD"}]""")]
    public void MissingRatesCurrenciesAndInvalidUnitsFailExplicitly(string lines) =>
        Assert.StartsWith("Error:", CostCalculationTools.CalculateCost(lines, "USD", "month"));
}
