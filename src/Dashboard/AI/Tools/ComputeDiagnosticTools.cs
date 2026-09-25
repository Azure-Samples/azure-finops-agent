using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Network Watcher connectivity check from an existing VM. It stays a typed tool because
/// connectivityCheck is an action POST that QueryAzure deliberately blocks.
/// </summary>
public sealed partial class ComputeDiagnosticTools(UserTokens tokens)
{
    [GeneratedRegex(@"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/\\?#%]+/providers/Microsoft\.Network/networkWatchers/[^/\\?#%]+$", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkWatcherId();

    [GeneratedRegex(@"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/\\?#%]+/providers/Microsoft\.Compute/virtualMachines/[^/\\?#%]+$", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualMachineId();

    public IEnumerable<AIFunction> Create() =>
    [
        AIFunctionFactory.Create(CheckVmConnectivity, nameof(CheckVmConnectivity),
            "Runs a bounded Network Watcher TCP connectivity diagnostic FROM an existing Azure VM (not from the agent host). Requires an existing regional Network Watcher, its VM agent extension and delegated RBAC; never creates prerequisites. An accepted diagnostic is not yet a completed result."),
    ];

    private async Task<string> CheckVmConnectivity(
        [Description("ARM resource ID of an existing Network Watcher in the source VM's region.")] string networkWatcherResourceId,
        [Description("ARM resource ID of the existing source VM.")] string sourceVmResourceId,
        [Description("Destination hostname or IP address requested by the user.")] string destinationAddress,
        [Description("Destination TCP port as a string, 1-65535.")] string destinationPort,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tokens.AzureToken)) return HttpHelper.TokenMissing("AzureToken", null, "network");
        if (!NetworkWatcherId().IsMatch(networkWatcherResourceId) || !VirtualMachineId().IsMatch(sourceVmResourceId)
            || Uri.CheckHostName(destinationAddress) == UriHostNameType.Unknown
            || !int.TryParse(destinationPort, out var port) || port is < 1 or > 65535)
            return "Error: provide valid Network Watcher and source VM resource IDs, destination host/IP, and TCP port.";

        var body = JsonSerializer.Serialize(new
        {
            source = new { resourceId = sourceVmResourceId },
            destination = new { address = destinationAddress, port },
            protocol = "Tcp",
            preferredIPVersion = "IPv4",
        });
        return await HttpHelper.SendWithRetryAsync(
            $"https://management.azure.com{networkWatcherResourceId}/connectivityCheck?api-version=2025-09-01",
            tokens.AzureToken, null, "network.connectivity", HttpMethod.Post, body, cancellationToken: cancellationToken);
    }
}