using System.Net;
using System.Text.Json;
using Azure.Core;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Runtime;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Endpoints;
using AzureFinOps.Dashboard.Jobs;
using AzureFinOps.Dashboard.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Tests;

/// <summary>The session endpoints for signed-out browsers, whose identity is only the number the host keeps in the session.</summary>
public sealed class SessionEndpointTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "finops-session-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _owned = [];
    private AgentSessionFactory _factory = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var telemetry = new AiTelemetry();
        var identity = new PersistentIdentity(new EphemeralDataProtectionProvider(), NullLogger<PersistentIdentity>.Instance, _root);
        _factory = AgentSessionFactory.Create(new SyntheticCredential(), telemetry, identity,
            new Uri("https://example.invalid/api/projects/synthetic"), "synthetic-model", "high", NullLoggerFactory.Instance, webSearch: false);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        _app = builder.Build();
        _app.UseSession();
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-User", out var id))
                context.Session.SetString("user", JsonSerializer.Serialize(new { id = long.Parse(id.ToString()), login = "signed-out" }));
            await next();
        });
        _app.MapSessionEndpoints(_factory, telemetry, new JobStore(NullLogger.Instance), NullLogger.Instance);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    [Fact]
    public async Task SignedOutConversationsAreSelectedAndReallyDeletedByTheirOwnerOnly()
    {
        long mineBrowser = NewBrowser(), otherBrowser = NewBrowser();
        var mine = await Create(mineBrowser);
        await mine.PublishAsync(new UserMessageEvent("Synthetic question"));
        var theirs = await Create(otherBrowser);
        await theirs.PublishAsync(new UserMessageEvent("Another synthetic question"));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/sessions/{mine.SessionId}/select", mineBrowser)).StatusCode);
        // Another browser's conversation is refused, never reported as done.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/sessions/{theirs.SessionId}/select", mineBrowser)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"/api/sessions/{theirs.SessionId}", mineBrowser)).StatusCode);
        Assert.Contains(await _factory.ListUserSessionsAsync(otherBrowser, null, null), item => item.SessionId == theirs.SessionId);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/sessions/{mine.SessionId}", mineBrowser)).StatusCode);
        Assert.DoesNotContain(await _factory.ListUserSessionsAsync(mineBrowser, null, null), item => item.SessionId == mine.SessionId);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"/api/sessions/{mine.SessionId}", mineBrowser)).StatusCode);
    }

    [Fact]
    public async Task ListNamesAskedConversationsByTheirQuestionAndHidesBlankDrafts()
    {
        var browser = NewBrowser();
        var asked = await Create(browser);
        await asked.PublishAsync(new UserMessageEvent("[CONTEXT: " + new string('x', 600) + "]\nWhat did I spend last month?"));
        var draft = await Create(browser);

        using var response = await Send(HttpMethod.Get, "/api/sessions", browser);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = json.RootElement.GetProperty("sessions").EnumerateArray().ToList();
        var row = Assert.Single(rows);
        Assert.Equal(asked.SessionId, row.GetProperty("id").GetString());
        Assert.Equal("What did I spend last month?", row.GetProperty("summary").GetString());
        Assert.DoesNotContain(rows, item => item.GetProperty("id").GetString() == draft.SessionId);
    }

    // Signed-out conversations live in the host's shared state root, so each test uses its own random browser identity
    // and removes the folders it created.
    private static long NewBrowser() => Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000);

    private async Task<AgentConversation> Create(long browser)
    {
        var conversation = await _factory.CreateNewAsync(browser, "signed-out", null, null);
        _owned.Add(conversation.WorkingDirectory);
        return conversation;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, long browser)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-User", browser.ToString());
        return _client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
        foreach (var directory in _owned.Distinct())
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private sealed class SyntheticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic tests never call the model.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic tests never call the model.");
    }
}

public sealed class UnmatchedEndpointTests
{
    [Fact]
    public async Task UnmatchedApiAndAuthPathsAre404WhileRoutesAndPagesAreUnaffected()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapGet("/api/known", () => "known");
        app.MapPost("/api/post-only", () => "posted");
        app.MapGet("/auth/me", () => "me");
        app.MapUnmatchedApiNotFound();
        app.MapFallback(() => Results.Content("<html>app</html>", "text/html"));
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal("known", await client.GetStringAsync("/api/known"));
        Assert.Equal("me", await client.GetStringAsync("/auth/me"));
        using var posted = await client.PostAsync("/api/post-only", null);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        foreach (var path in new[] { "/api/unknown", "/api/known/extra", "/api/post-only", "/auth/unknown" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Not found", body.RootElement.GetProperty("error").GetString());
        }

        using var page = await client.GetAsync("/some-page");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
    }
}
