using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

/// <summary>
/// Microsoft Learn's MCP tools reach the agent unchanged; the host only bounds what a page read adds to the context and
/// classifies the server's own error flag as a failure.
/// </summary>
public sealed class MicrosoftLearnMcpTests
{
    [Fact]
    public void OnlyTheDocumentationSearchAndFetchToolsAreUsed()
    {
        Assert.Equal(["microsoft_docs_search", "microsoft_docs_fetch"], MicrosoftLearnMcp.ToolNames);
        Assert.True(MicrosoftLearnMcp.IsLearnTool("microsoft_docs_fetch"));
        Assert.False(MicrosoftLearnMcp.IsLearnTool("microsoft_code_sample_search"));
        Assert.False(MicrosoftLearnMcp.IsLearnTool(null));
        Assert.Equal("https://learn.microsoft.com/api/mcp", MicrosoftLearnMcp.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void ALongPageIsCutWithANoteAndShortResultsStayUnchanged()
    {
        var shortResult = new TextContent("# Page\nShort.");
        Assert.Same(shortResult, MicrosoftLearnMcp.Bound(shortResult));
        Assert.Equal("plain", MicrosoftLearnMcp.Bound("plain"));

        var page = "# Long page\n" + new string('x', MicrosoftLearnMcp.MaxResultCharacters * 2);
        foreach (var longResult in new object[]
        {
            new TextContent(page),
            new AIContent[] { new TextContent(page[..100]), new TextContent(page[100..]) },
            JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = page } }, isError = false }),
        })
        {
            var bounded = Assert.IsType<string>(MicrosoftLearnMcp.Bound(longResult));
            Assert.StartsWith("# Long page\n", bounded);
            Assert.Contains("[Cut at 48 KB of 96 KB. Search for the passage you need with microsoft_docs_search", bounded);
            Assert.True(bounded.Length < MicrosoftLearnMcp.MaxResultCharacters + 300);
        }
    }

    [Fact]
    public void AStructuredResultReachesTheModelOnceAndAnErrorKeepsItsFlag()
    {
        var results = """{"results":[{"title":"Soft-delete overview","contentUrl":"https://learn.microsoft.com/azure/key-vault/general/soft-delete-overview"}]}""";
        var structured = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = results } },
            structuredContent = JsonSerializer.Deserialize<JsonElement>(results),
            isError = false,
        });
        Assert.Equal(results, Assert.IsType<string>(MicrosoftLearnMcp.Bound(structured)));

        var error = JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = "Invalid URL" } }, isError = true });
        Assert.Equal(error.GetRawText(), Assert.IsType<JsonElement>(MicrosoftLearnMcp.Bound(error)).GetRawText());
        Assert.False(EvidenceInspector.Inspect(AzureFinOps.Dashboard.AI.Runtime.AgentConversation.ResultText(MicrosoftLearnMcp.Bound(error))!).Success);
    }

    [Fact]
    public void AnMcpErrorResultIsAFailedCall()
    {
        Assert.False(EvidenceInspector.Inspect("""{"content":[{"type":"text","text":"Invalid URL"}],"isError":true}""").Success);
        Assert.True(EvidenceInspector.Inspect("""{"content":[{"type":"text","text":"# Page"}],"isError":false}""").Success);
        // API data with an isError column (a KQL projection, for example) is not a failed call.
        Assert.True(EvidenceInspector.Inspect("HTTP 200 OK\n" + """{"rows":[{"operation":"GET /","isError":true}]}""").Success);
    }

    [Fact]
    public void ALearnResultReachesTheTranscriptAsItsText()
    {
        Assert.Equal("# Page", AzureFinOps.Dashboard.AI.Runtime.AgentConversation.ResultText(new TextContent("# Page")));
        Assert.Equal("a\nb", AzureFinOps.Dashboard.AI.Runtime.AgentConversation.ResultText(new AIContent[] { new TextContent("a"), new TextContent("b") }));
        Assert.Equal("plain", AzureFinOps.Dashboard.AI.Runtime.AgentConversation.ResultText("plain"));
        Assert.Null(AzureFinOps.Dashboard.AI.Runtime.AgentConversation.ResultText(null));
        Assert.Equal("""{"isError":true}""",
            AzureFinOps.Dashboard.AI.Runtime.AgentConversation.ResultText(JsonSerializer.SerializeToElement(new { isError = true })));
    }

    [Fact]
    public void DocumentationToolsJoinTheOwnersToolsForARun()
    {
        var docs = AIFunctionFactory.Create((string query) => "passages", "microsoft_docs_search");
        var identity = new AzureFinOps.Dashboard.Auth.PersistentIdentity(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureFinOps.Dashboard.Auth.PersistentIdentity>.Instance,
            Path.Combine(Path.GetTempPath(), "finops-learn-" + Guid.NewGuid().ToString("N")));
        var factory = AzureFinOps.Dashboard.AI.AgentSessionFactory.Create(new SyntheticCredential(), new AzureFinOps.Dashboard.Observability.AiTelemetry(), identity,
            new Uri("https://example.invalid/api/projects/synthetic"), "synthetic-model", null,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, webSearch: false);

        var tools = factory.RunOptions(101, lightweight: false, [docs]).ChatOptions!.Tools!;

        Assert.Contains(tools, tool => tool.Name == "QueryAzure");
        Assert.Contains(tools, tool => tool.Name == "microsoft_docs_search");
        Assert.DoesNotContain(tools, tool => tool.Name == "SuggestFollowUp");
        Assert.DoesNotContain(factory.RunOptions(101, lightweight: false).ChatOptions!.Tools!, tool => tool.Name == "microsoft_docs_search");
    }

    private sealed class SyntheticCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
