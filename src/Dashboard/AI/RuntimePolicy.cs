using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;

#pragma warning disable GHCP001

namespace AzureFinOps.Dashboard.AI;

internal static class RuntimePolicy
{
    internal static void Apply(SessionConfigBase config)
    {
        var toolNames = config.Tools?.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        config.AvailableTools = toolNames.Select(name => $"custom:{name}").ToList();
        config.ExcludedTools = ["builtin:*", "mcp:*"];
        config.ToolSearch = new ToolSearchConfig { Enabled = false };
        config.EnableSessionStore = false;
        config.Memory = new MemoryConfiguration { Enabled = false };
        config.ManagedSettings = new ManagedSettings
        {
            Permissions = new ManagedSettingsPermissions { DisableBypassPermissionsMode = "disable" }
        };
        config.OnPermissionRequest = (request, invocation) => Task.FromResult(
            request is PermissionRequestCustomTool tool
                && toolNames.Contains(tool.ToolName)
                && request.ManagedApprovalRequired is not true
                    ? PermissionDecision.ApproveOnce()
                    : PermissionDecision.Reject("Only registered application tools are permitted. Host execution and filesystem access are disabled."));
        config.Hooks = new SessionHooks
        {
            OnPreToolUse = (input, invocation) => Task.FromResult<PreToolUseHookOutput?>(new()
            {
                PermissionDecision = toolNames.Contains(input.ToolName)
                    && !SensitiveContent.ContainsSecret(JsonSerializer.Serialize(input.ToolArgs)) ? "allow" : "deny",
                ModifiedArgs = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    SensitiveContent.Redact(JsonSerializer.Serialize(input.ToolArgs))),
                AdditionalContext = "Never send credentials to tools. Use only host-registered operations."
            })
        };
    }
}