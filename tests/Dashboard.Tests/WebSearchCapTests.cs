using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.AI.Projects;
using Azure.Core;
using AzureFinOps.Dashboard.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable OPENAI001 // The Responses client Agent Framework's Foundry agent uses underneath.

namespace Dashboard.Tests;

/// <summary>
/// The hosted web search cap travels as the Responses API's max_tool_calls on every model request of a turn,
/// through Agent Framework's option merge and function-invocation loop, and never replaces the host's tools.
/// </summary>
public sealed class WebSearchCapTests
{
    [Fact]
    public async Task EveryModelRequestCarriesTheWebSearchCapBesideTheFunctionTools()
    {
        var bodies = new List<JsonObject>();
        var agent = Agent(bodies, webSearch: true);
        var tool = AIFunctionFactory.Create((string region) => $"price for {region}", "get_price");

        var response = await agent.RunAsync("price?", options: new ChatClientAgentRunOptions(new ChatOptions { Tools = [tool] }));

        Assert.Equal("done", response.Text);
        Assert.Equal(2, bodies.Count);
        foreach (var body in bodies)
        {
            Assert.Equal(AgentSessionFactory.WebSearchCallsPerResponse, body["max_tool_calls"]!.GetValue<int>());
            var tools = body["tools"]!.AsArray().Select(item => item!["type"]!.GetValue<string>()).ToList();
            Assert.Contains("web_search", tools);
            Assert.Contains("function", tools);
            Assert.Equal("gpt-6-sol", body["model"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task WithoutWebSearchNoCapIsSent()
    {
        var bodies = new List<JsonObject>();
        var agent = Agent(bodies, webSearch: false);

        await agent.RunAsync("price?", options: new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string region) => $"price for {region}", "get_price")],
        }));

        Assert.All(bodies, body => Assert.Null(body["max_tool_calls"]));
    }

    // The model re-checked Learn results with site:learn.microsoft.com web searches despite three prompt rules; after a Learn
    // call, the turn's later requests no longer offer hosted web search, while its function tools stay.
    [Fact]
    public async Task AfterAMicrosoftLearnCallTheTurnsLaterRequestsLeaveWebSearchOut()
    {
        var bodies = new List<JsonObject>();
        var agent = Agent(bodies, webSearch: true, functionName: "microsoft_docs_search");
        using var turn = new AzureFinOps.Dashboard.Infrastructure.ToolExecutionContext("synthetic-session", 101, CancellationToken.None);

        var response = await agent.RunAsync("Is purge protection on by default?", options: new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string query) => "passages", "microsoft_docs_search")],
        }));

        Assert.Equal("done", response.Text);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("web_search", ToolTypes(bodies[0]));
        Assert.DoesNotContain("web_search", ToolTypes(bodies[1]));
        Assert.Contains("function", ToolTypes(bodies[1]));
        Assert.True(turn.MicrosoftLearnCalled);

        // The next turn starts with web search offered again.
        bodies.Clear();
        using var next = new AzureFinOps.Dashboard.Infrastructure.ToolExecutionContext("synthetic-session", 101, CancellationToken.None);
        await Agent(bodies, webSearch: true).RunAsync("price?", options: new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string region) => $"price for {region}", "get_price")],
        }));
        Assert.All(bodies, body => Assert.Contains("web_search", ToolTypes(body)));
    }

    private static List<string> ToolTypes(JsonObject body) =>
        body["tools"]!.AsArray().Select(item => item!["type"]!.GetValue<string>()).ToList();

    private static AIAgent Agent(List<JsonObject> bodies, bool webSearch, string functionName = "get_price")
    {
        // The same construction as production: the Foundry project client's agent over its Responses API.
        var project = new AIProjectClient(new Uri("https://account.services.ai.azure.com/api/projects/project"), new FakeCredential(),
            new AIProjectClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(new FakeResponses(bodies, functionName))) });
        return project.AsAIAgent(new ChatClientAgentOptions { ChatOptions = AgentSessionFactory.AgentChatOptions("gpt-6-sol", null, webSearch) },
            clientFactory: AgentSessionFactory.ModelClient);
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    // Answers the first request with a call to the named function and the next with a final message.
    private sealed class FakeResponses(List<JsonObject> bodies, string functionName) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            bodies.Add(body);
            var output = bodies.Count == 1
                ? $$"""[{"type":"function_call","id":"fc_1","call_id":"call_1","name":"{{functionName}}","arguments":"{\"region\":\"eastus\",\"query\":\"purge protection\"}","status":"completed"}]"""
                : """[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"done","annotations":[]}]}]""";
            var json = $$$"""{"id":"resp_{{{bodies.Count}}}","object":"response","created_at":1,"status":"completed","model":"gpt-6-sol","output":{{{output}}},"parallel_tool_calls":true,"tool_choice":"auto","tools":[],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
