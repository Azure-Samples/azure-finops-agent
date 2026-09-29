using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Runtime;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
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
        await conversation.PublishAsync(new ToolStartEvent("call-1", "QueryAzure", "{}"));
        await conversation.PublishAsync(new ToolCompleteEvent("call-1", true, "{}", null));
        await conversation.PublishAsync(new AssistantMessageEvent("m", "Synthetic answer"));
        await conversation.PublishAsync(new TurnIdleEvent());

        var events = await _factory.LoadTranscriptAsync(conversation.SessionId, userId, Tenant, owner);
        Assert.Equal([typeof(UserMessageEvent), typeof(ToolStartEvent), typeof(ToolCompleteEvent), typeof(AssistantMessageEvent), typeof(TurnIdleEvent)],
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