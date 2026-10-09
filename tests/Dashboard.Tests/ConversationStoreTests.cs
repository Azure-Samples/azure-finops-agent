using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Runtime;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using AzureFinOps.Dashboard.Observability;
using Azure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Tests;

public sealed class ConversationStoreTests : IAsyncLifetime
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "finops-conversations-" + Guid.NewGuid().ToString("N"));
    private AgentSessionFactory _factory = null!;

    public Task InitializeAsync()
    {
        var identity = new PersistentIdentity(new EphemeralDataProtectionProvider(), NullLogger<PersistentIdentity>.Instance, _root);
        _factory = AgentSessionFactory.Create(new SyntheticCredential(), new AiTelemetry(), identity,
            new Uri("https://example.invalid/api/projects/synthetic"), "synthetic-model", "high", NullLoggerFactory.Instance, webSearch: false);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TranscriptRoundTripsForTheOwnerOnly()
    {
        var owner = Guid.NewGuid().ToString();
        var userId = PersistentIdentity.DeriveUserId(Tenant, owner);
        var conversation = await _factory.CreateNewAsync(userId, "synthetic", Tenant, owner);
        await conversation.PublishAsync(new UserMessageEvent("Synthetic question"));
        await conversation.PublishAsync(new MessageDeltaEvent("m", "live only"));
        await conversation.PublishAsync(new ReasoningDeltaEvent("live only"));
        await conversation.PublishAsync(new ReasoningEvent("**Synthetic thinking**"));
        await conversation.PublishAsync(new ToolStartEvent("call-1", "QueryAzure", "{}"));
        await conversation.PublishAsync(new ToolCompleteEvent("call-1", true, "{}", null));
        await conversation.PublishAsync(new AssistantMessageEvent("m", "Synthetic answer"));
        await conversation.PublishAsync(new TurnIdleEvent());

        var events = await _factory.LoadTranscriptAsync(conversation.SessionId, userId, Tenant, owner);
        Assert.Equal([typeof(UserMessageEvent), typeof(ReasoningEvent), typeof(ToolStartEvent), typeof(ToolCompleteEvent), typeof(AssistantMessageEvent), typeof(TurnIdleEvent)],
            events.Select(item => item.GetType()));

        var other = Guid.NewGuid().ToString();
        var otherId = PersistentIdentity.DeriveUserId(Tenant, other);
        Assert.False(await _factory.UserOwnsSessionAsync(otherId, Tenant, other, conversation.SessionId));
        await Assert.ThrowsAsync<AgentSessionFactory.HistoryUnavailableException>(() =>
            _factory.LoadTranscriptAsync(conversation.SessionId, otherId, Tenant, other));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _factory.DeleteUserSessionAsync(otherId, Tenant, other, conversation.SessionId));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("../escape")]
    public async Task MissingOrInvalidHistoryIsNotAnEmptyConversation(string sessionId)
    {
        var owner = Guid.NewGuid().ToString();
        var userId = PersistentIdentity.DeriveUserId(Tenant, owner);
        await Assert.ThrowsAsync<AgentSessionFactory.HistoryUnavailableException>(() =>
            _factory.LoadTranscriptAsync(sessionId, userId, Tenant, owner));
    }

    [Fact]
    public async Task PersistedConversationReopensAfterRestartAndDeletes()
    {
        var owner = Guid.NewGuid().ToString();
        var userId = PersistentIdentity.DeriveUserId(Tenant, owner);
        var conversation = await _factory.CreateNewAsync(userId, "synthetic", Tenant, owner);
        await conversation.PublishAsync(new UserMessageEvent("Synthetic question"));
        await _factory.DisposeAsync();
        await InitializeAsync();

        var listed = Assert.Single(await _factory.ListUserSessionsAsync(userId, Tenant, owner));
        Assert.Equal(conversation.SessionId, listed.SessionId);
        var reopened = await _factory.GetOrResumeAsync(userId, conversation.SessionId, "synthetic", Tenant, owner);
        Assert.Single(await reopened.GetEventsAsync());

        await _factory.DeleteUserSessionAsync(userId, Tenant, owner, conversation.SessionId);
        Assert.Empty(await _factory.ListUserSessionsAsync(userId, Tenant, owner));
    }

    [Fact]
    public async Task AnonymousConversationsRemainListedAfterCompletionAndBelongOnlyToTheirBrowserIdentity()
    {
        var owner = PersistentIdentity.DeriveUserId(Tenant, Guid.NewGuid().ToString());
        var other = PersistentIdentity.DeriveUserId(Tenant, Guid.NewGuid().ToString());
        var conversation = await _factory.CreateNewAsync(owner, "synthetic", null, null);
        try
        {
            await conversation.PublishAsync(new UserMessageEvent("Synthetic question"));
            await conversation.PublishAsync(new AssistantMessageEvent("answer", "Synthetic answer"));
            await conversation.PublishAsync(new TurnIdleEvent());
            Assert.Contains(await _factory.ListUserSessionsAsync(owner, null, null),
                item => item.SessionId == conversation.SessionId);
            Assert.DoesNotContain(await _factory.ListUserSessionsAsync(other, null, null),
                item => item.SessionId == conversation.SessionId);
            Assert.False(await _factory.UserOwnsSessionAsync(other, null, null, conversation.SessionId));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                _factory.DeleteUserSessionAsync(other, null, null, conversation.SessionId));
        }
        finally
        {
            await _factory.DeleteUserSessionAsync(owner, null, null, conversation.SessionId);
        }
        Assert.DoesNotContain(await _factory.ListUserSessionsAsync(owner, null, null),
            item => item.SessionId == conversation.SessionId);
        // The signed-out store is shared across test runs (listing even recreates the owner folder), so remove both
        // identities' folders last.
        foreach (var directory in new[] { conversation.WorkingDirectory, Path.Combine(Path.GetDirectoryName(conversation.WorkingDirectory)!, other.ToString()) })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void SummaryKeepsTheUsersWordsWhenTheContextBlockFillsTheCap()
    {
        var context = "[CONTEXT: Azure NOT connected. " + new string('x', 600) + "]\n[Answer in one short sentence.]\n";
        Assert.Equal("hi", AgentConversation.SummaryFor(context + "hi"));
        Assert.Equal(400, AgentConversation.SummaryFor(context + new string('q', 900))!.Length);
        Assert.Null(AgentConversation.SummaryFor(context));
        Assert.Null(AgentConversation.SummaryFor("   "));
    }

    [Fact]
    public async Task ListedSummaryIsRepairedFromTheFirstQuestionAndBlankDraftsHaveNone()
    {
        var owner = Guid.NewGuid().ToString();
        var userId = PersistentIdentity.DeriveUserId(Tenant, owner);
        var asked = await _factory.CreateNewAsync(userId, "synthetic", Tenant, owner);
        await asked.PublishAsync(new UserMessageEvent("[CONTEXT: " + new string('x', 600) + "]\nWhat did I spend last month?"));
        var draft = await _factory.CreateNewAsync(userId, "synthetic", Tenant, owner);

        // The value older builds stored: the first 400 characters of the prompt, all of it context.
        var metaPath = Path.Combine(asked.WorkingDirectory, "sessions", asked.SessionId, "session.json");
        var meta = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metaPath))!;
        meta["summary"] = "[CONTEXT: " + new string('x', 389);
        File.WriteAllText(metaPath, meta.ToJsonString());

        var listed = (await _factory.ListUserSessionsAsync(userId, Tenant, owner)).ToDictionary(item => item.SessionId);
        Assert.Equal("What did I spend last month?", listed[asked.SessionId].Summary);
        Assert.Null(listed[draft.SessionId].Summary);
    }

    [Fact]
    public void EachRunExposesPlainToolsAndOnlyChangesNeedApproval()
    {
        var options = _factory.RunOptions(101, lightweight: false);
        var tools = options.ChatOptions!.Tools!;
        Assert.Contains(tools, tool => tool.Name == "QueryAzure");
        Assert.IsType<ApprovalRequiredAIFunction>(Assert.Single(tools, tool => tool.Name == "ApplyAzureChange"));
        Assert.Single(tools, tool => tool is ApprovalRequiredAIFunction);
        Assert.Null(options.ChatOptions.Reasoning);
        Assert.Equal(ReasoningEffort.Low, _factory.RunOptions(101, lightweight: true).ChatOptions!.Reasoning!.Effort);

        var invoker = _factory.Agent.GetService<FunctionInvokingChatClient>();
        Assert.NotNull(invoker);
        Assert.True(invoker.IncludeDetailedErrors);
        Assert.NotNull(invoker.FunctionInvoker);
    }

    [Fact]
    public async Task ToolCompletionCarriesEachCallsOwnExecutionTime()
    {
        var owner = Guid.NewGuid().ToString();
        var userId = PersistentIdentity.DeriveUserId(Tenant, owner);
        var conversation = await _factory.CreateNewAsync(userId, "synthetic", Tenant, owner);
        var invoker = _factory.Agent.GetService<FunctionInvokingChatClient>()!;
        var slow = AIFunctionFactory.Create(async () => { await Task.Delay(300); return "slow"; }, "Slow");
        var fast = AIFunctionFactory.Create(() => "fast", "Fast");

        using (new ToolExecutionContext(conversation.SessionId, userId, CancellationToken.None))
        {
            // Parallel calls finish together in the stream; each completion must still report its own run time.
            await Task.WhenAll(
                invoker.FunctionInvoker!(new FunctionInvocationContext { Function = slow, Arguments = [], CallContent = new FunctionCallContent("slow-1", "Slow") }, CancellationToken.None).AsTask(),
                invoker.FunctionInvoker!(new FunctionInvocationContext { Function = fast, Arguments = [], CallContent = new FunctionCallContent("fast-1", "Fast") }, CancellationToken.None).AsTask());
            await conversation.ToolCompletedAsync("slow-1", true, "slow", null);
            await conversation.ToolCompletedAsync("fast-1", true, "fast", null);
        }
        await conversation.ToolCompletedAsync("outside-1", true, "{}", null);

        var done = (await conversation.GetEventsAsync()).OfType<ToolCompleteEvent>().ToDictionary(item => item.CallId);
        Assert.True(done["slow-1"].DurationMs >= 250, $"slow call measured {done["slow-1"].DurationMs} ms");
        Assert.True(done["fast-1"].DurationMs < done["slow-1"].DurationMs);
        Assert.Null(done["outside-1"].DurationMs);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
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