using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Tests;

/// <summary>Every model call carries the tools it is given, so a turn gets only the ones it can use.</summary>
public sealed class TurnScopeTests
{
    private const long User = 4242;
    private readonly AiTelemetry _telemetry = new();
    private readonly AgentSessionFactory _factory;

    public TurnScopeTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-scope-" + Guid.NewGuid().ToString("N"));
        var identity = new PersistentIdentity(new EphemeralDataProtectionProvider(), NullLogger<PersistentIdentity>.Instance, root);
        _factory = AgentSessionFactory.Create(new SyntheticCredential(), _telemetry, identity,
            new Uri("https://example.invalid/api/projects/synthetic"), "synthetic-model", "medium", NullLoggerFactory.Instance, webSearch: true);
    }

    private string[] Names(AgentSessionFactory.TurnScope? scope) =>
        _factory.RunOptions(User, false, null, scope).ChatOptions!.Tools!.Select(tool => tool.Name).ToArray();

    [Fact]
    public void EveryFilteredToolExists()
    {
        var all = Names(null);
        foreach (var name in AgentSessionFactory.AzureConnectedTools.Append("ReportJobOutcome").Append("QueryUploadedFile"))
            Assert.Contains(name, all);
    }

    [Fact]
    public void ASignedOutChatGetsNoToolThatNeedsAzureAJobOrAFile()
    {
        var names = Names(new(AzureConnected: false, Scheduled: false, DataUploads: false));

        Assert.Empty(names.Intersect(AgentSessionFactory.AzureConnectedTools));
        Assert.DoesNotContain("ReportJobOutcome", names);
        Assert.DoesNotContain("QueryUploadedFile", names);
        foreach (var kept in new[] { "QueryAzure", "RenderChart", "RenderAdvancedChart", "GenerateScript", "GenerateHtmlPresentation", "GenerateDataReport" })
            Assert.Contains(kept, names);
    }

    [Fact]
    public void AConnectedChatKeepsTheAzureToolsAndAFileTurnTheFileTool()
    {
        var chat = Names(new(AzureConnected: true, Scheduled: false, DataUploads: false));
        Assert.Subset(chat.ToHashSet(), AgentSessionFactory.AzureConnectedTools.ToHashSet());
        Assert.DoesNotContain("ReportJobOutcome", chat);
        Assert.DoesNotContain("QueryUploadedFile", chat);

        Assert.Contains("QueryUploadedFile", Names(new(AzureConnected: false, Scheduled: false, DataUploads: true)));
        Assert.Equal(Names(null), Names(new(AzureConnected: true, Scheduled: true, DataUploads: true)));
    }

    [Fact]
    public void TheScopeFollowsTheOwnersAzureToken()
    {
        Assert.False(_factory.ScopeFor(User, "synthetic-session").AzureConnected);
        _telemetry.UserTokens[User] = new UserTokens { UserId = User, AzureToken = "synthetic" };
        var scope = _factory.ScopeFor(User, "synthetic-session");
        Assert.True(scope.AzureConnected);
        Assert.False(scope.Scheduled);
        Assert.False(scope.DataUploads);
    }

    [Fact]
    public void ASignedOutChatSendsUnderSixtyPercentOfTheToolText()
    {
        static int Size(IEnumerable<AITool> tools) => tools.Sum(tool =>
            tool.Name.Length + (tool.Description?.Length ?? 0)
            + (tool is AIFunctionDeclaration function ? function.JsonSchema.ToString().Length : 0));
        var all = Size(_factory.RunOptions(User, false).ChatOptions!.Tools!);
        var signedOut = Size(_factory.RunOptions(User, false, null, new(false, false, false)).ChatOptions!.Tools!);
        Assert.True(signedOut < all * 0.6, $"signed-out tool text {signedOut} of {all} characters");
    }

    private sealed class SyntheticCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
