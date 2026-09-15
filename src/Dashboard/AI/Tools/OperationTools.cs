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
            "Check one host-registered Azure operation by opaque operationId. Polls only the stored ARM URL under the caller's delegated token; never accepts a URL or repeats a mutation. Respect nextPollUtc. Accepted, unknown and in-progress operations are not completed.");
        yield return AIFunctionFactory.Create(ListOperationResults, "ListOperationResults",
            "List up to 100 recent owner-bound operations from this conversation, including prerequisite resources from partial deployments. Use after a failed VM or multi-resource change to account for what was created and generate a reviewed cleanup script. Never delete automatically.");
    }

    private async Task<string> GetOperationStatus([Description("Opaque operationId returned by an earlier Azure mutation or diagnostic.")] string operationId,
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