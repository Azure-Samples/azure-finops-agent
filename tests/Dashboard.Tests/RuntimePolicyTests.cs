using AzureFinOps.Dashboard.AI;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class RuntimePolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateAndResumeExposeOnlyHostTools(bool resume)
    {
        SessionConfigBase config = resume ? new ResumeSessionConfig() : new SessionConfig();
        config.Tools = [AIFunctionFactory.Create(() => "synthetic result", "ApprovedRead")];
        RuntimePolicy.Apply(config);
        Assert.Equal(["custom:ApprovedRead"], config.AvailableTools);
        Assert.Contains("builtin:*", config.ExcludedTools!);
        Assert.Contains("mcp:*", config.ExcludedTools!);
        Assert.False(config.EnableSessionStore);
        Assert.False(config.Memory!.Enabled);
        Assert.False(config.ToolSearch!.Enabled);
        Assert.Equal("disable", config.ManagedSettings!.Permissions!.DisableBypassPermissionsMode);
        Assert.NotNull(config.OnPermissionRequest);
        Assert.NotNull(config.Hooks!.OnPreToolUse);
    }
}