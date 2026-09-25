using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class SensitiveContentTests
{
    [Theory]
    [InlineData("My password is synthetic-test-only")]
    [InlineData("Authorization: Bearer synthetic-test-token")]
    [InlineData("https://example.test/file?sig=synthetic-test-signature&sp=r")]
    [InlineData("AccountName=test;AccountKey=synthetic-test-key;EndpointSuffix=example.test")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nsynthetic-test-key\n-----END PRIVATE KEY-----")]
    [InlineData("{\"properties\":{\"adminPassword\":\"synthetic-test-only\"}}")]
    [InlineData("{\"items\":[{\"client_secret\":\"synthetic-test-only\"}]}")]
    [InlineData("{\"body\":\"{\\\"refresh_token\\\":\\\"synthetic-test-only\\\"}\"}")]
    public void RecognizableSecretsAreRemoved(string input)
    {
        Assert.True(SensitiveContent.ContainsSecret(input));
        var cleaned = SensitiveContent.Redact(input);
        Assert.DoesNotContain("synthetic-test", cleaned);
        Assert.Contains("[REDACTED]", cleaned);
        Assert.Equal(cleaned, SensitiveContent.Redact(cleaned));
    }

    [Theory]
    [InlineData("How do I reset my password?")]
    [InlineData("Compare 1000000 input tokens and 500 output tokens")]
    [InlineData("{\"inputTokens\":1000,\"cost\":42.73,\"currency\":\"USD\"}")]
    [InlineData("SSH public key: ssh-ed25519 AAAA-test-public-key")]
    public void OrdinaryQuestionsAndCostsAreUnchanged(string input)
    {
        Assert.False(SensitiveContent.ContainsSecret(input));
        Assert.Equal(input, SensitiveContent.Redact(input));
    }

    [Fact]
    public async Task ProtectedToolsRejectSecretsBeforeCallingInnerTool()
    {
        var calls = 0;
        var tool = new ProtectedTool(AIFunctionFactory.Create((string body) => { calls++; return body; }, "Echo"));
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["body"] = "{\"adminPassword\":\"synthetic-test-only\"}" });
        Assert.Equal(0, calls);
        Assert.Equal(SensitiveContent.RejectedMessage, result);
    }

    [Fact]
    public async Task ProtectedToolsRedactNestedResults()
    {
        var tool = new ProtectedTool(AIFunctionFactory.Create(() => "{\"total\":42.73,\"secretValue\":\"synthetic-test-only\"}", "Read"));
        var result = await tool.InvokeAsync(new AIFunctionArguments());
        var wrapped = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.String, wrapped.ValueKind);
        using var parsed = JsonDocument.Parse(wrapped.GetString()!);
        Assert.Equal(42.73m, parsed.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal("[REDACTED]", parsed.RootElement.GetProperty("secretValue").GetString());
    }
}