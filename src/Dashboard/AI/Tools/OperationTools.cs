using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public sealed class OperationTools(UserTokens tokens)
{
    private static readonly HttpClient PollClient = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
    { Timeout = TimeSpan.FromSeconds(15) };
    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GetOperationStatus, "GetOperationStatus",
            "Check one exact host-registered Azure operation by opaque operationId. Prefer this targeted lookup over listing operations when the ID is already known. Polls only the stored ARM URL under the caller's delegated token; never accepts a URL or repeats a mutation. Respect nextPollUtc, reuse terminal results, and do not poll unrelated operations. Accepted, unknown and in-progress operations are not completed.");
        yield return AIFunctionFactory.Create(ListOperationResults, "ListOperationResults",
            "List up to 100 recent owner-bound operations in the current conversation, including prerequisite resources from partial deployments. This fixed host-scoped list has no status, subscription or date filter; reuse it once and use GetOperationStatus for a known operation ID rather than repeatedly listing everything. It is not an all-Azure inventory or complete history beyond 100 entries. Use after a failed multi-resource change to account for prerequisites and generate a reviewed cleanup script. Never delete automatically.");
    }

    private async Task<string> GetOperationStatus([Description("Exact opaque operationId returned by the earlier mutation or diagnostic being checked. Reuse this ID; do not list or poll unrelated operations.")] string operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = OperationStore.Default.Find(operationId, tokens.UserId);
        if (operation is null) return "Error: operation not found or unavailable.";
        if (operation.Status is "awaitingApproval" or "expired" or "rejected" or "succeeded" or "failed" || operation.NextPollUtc > DateTimeOffset.UtcNow) return OperationStore.Envelope(operation);
        if (string.IsNullOrEmpty(tokens.AzureToken)) return HttpHelper.TokenMissing("AzureToken", null, "operation");
        using var request = new HttpRequestMessage(HttpMethod.Get, operation.PollUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AzureToken);
        using var response = await PollClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var updated = OperationStore.Default.ObserveResponse(operation, response, body, polling: true);
        return OperationStore.Envelope(updated);
    }

    private string ListOperationResults()
    {
        var sessionId = ToolExecutionContext.Current?.SessionId;
        if (sessionId is null) return "Error: no active conversation.";
        return JsonSerializer.Serialize(new
        {
            operations = OperationStore.Default.ForSession(tokens.UserId, sessionId).Select(operation => JsonSerializer.Deserialize<JsonElement>(OperationStore.Envelope(operation))),
            limit = 100,
            cleanup = "Use resource IDs from these results in a reviewed script. A failed allocation can leave successfully created prerequisites. No cleanup was executed."
        });
    }
}