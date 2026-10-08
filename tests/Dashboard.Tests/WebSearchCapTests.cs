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

    private static AIAgent Agent(List<JsonObject> bodies, bool webSearch)
    {
        // The same construction as production: the Foundry project client's agent over its Responses API.
        var project = new AIProjectClient(new Uri("https://account.services.ai.azure.com/api/projects/project"), new FakeCredential(),
            new AIProjectClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(new FakeResponses(bodies))) });
        return project.AsAIAgent(new ChatClientAgentOptions { ChatOptions = AgentSessionFactory.AgentChatOptions("gpt-6-sol", null, webSearch) });
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    // Answers the first request with a get_price function call and the next with a final message.
    private sealed class FakeResponses(List<JsonObject> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            bodies.Add(body);
            var output = bodies.Count == 1
                ? """[{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_price","arguments":"{\"region\":\"eastus\"}","status":"completed"}]"""
                : """[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"done","annotations":[]}]}]""";
            var json = $$$"""{"id":"resp_{{{bodies.Count}}}","object":"response","created_at":1,"status":"completed","model":"gpt-6-sol","output":{{{output}}},"parallel_tool_calls":true,"tool_choice":"auto","tools":[],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
