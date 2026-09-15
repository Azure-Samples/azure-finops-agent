using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.Endpoints;

public static class OperationEndpoints
{
    public static void MapOperationEndpoints(this IEndpointRouteBuilder app, CopilotSessionFactory factory, SessionTokenStore tokenStore)
    {
        app.MapPost("/api/changes/{id}/approve", async (HttpContext context, string id, IHttpClientFactory httpFactory) =>
        {
            if (!TryUser(context, out var userId, out var oid)) return Results.Unauthorized();
            if (string.IsNullOrEmpty(oid)) return Results.Unauthorized();
            var proposal = OperationStore.Default.Find(id, userId);
            if (proposal is null || !await factory.UserOwnsSessionAsync(userId, oid, proposal.SessionId, context.RequestAborted)) return Results.NotFound();
            JsonElement approval;
            try { approval = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(new { error = "Explicit review acknowledgement is required." }); }
            if (approval.ValueKind != JsonValueKind.Object || !approval.TryGetProperty("acknowledgeCostImpact", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
                return Results.BadRequest(new { error = "Review the configuration and potential charges before approving." });
            if (!TurnExecution.TryBegin(proposal.SessionId, userId, null, out var turn))
                return Results.Conflict(new { error = "Wait for the active turn to finish before approving a change." });
            try
            {
                var token = await tokenStore.GetAzureTokenAsync(context, httpFactory);
                if (string.IsNullOrEmpty(token)) return Results.Unauthorized();
                var operation = OperationStore.Default.Approve(id, userId, proposal.SessionId);
                if (operation is null) return Results.Conflict(new { error = "The proposal expired or was already reviewed. Request a fresh plan." });
                using var execution = new ToolExecutionContext(operation.SessionId, userId, context.RequestAborted, operation.Id);
                await HttpHelper.SendWithRetryAsync(operation.ResourceUrl, token, null, "operation.approved",
                    new HttpMethod(operation.Method), operation.PendingBody, cancellationToken: context.RequestAborted);
                var current = OperationStore.Default.Find(id, userId)!;
                return Results.Ok(new { result = JsonSerializer.Deserialize<JsonElement>(OperationStore.Envelope(current)) });
            }
            finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
        });

        app.MapPost("/api/changes/{id}/reject", (HttpContext context, string id) =>
        {
            if (!TryUser(context, out var userId, out _)) return Results.Unauthorized();
            return OperationStore.Default.Reject(id, userId) ? Results.Ok(new { rejected = true }) : Results.NotFound();
        });
    }

    private static bool TryUser(HttpContext context, out long userId, out string? oid)
    {
        userId = 0;
        oid = null;
        try
        {
            using var user = JsonDocument.Parse(context.Session.GetString("user") ?? "{}");
            if (!user.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt64(out userId)) return false;
            using var azure = JsonDocument.Parse(context.Session.GetString("azure_user") ?? "{}");
            if (azure.RootElement.TryGetProperty("objectId", out var identifier)) oid = identifier.GetString();
            return true;
        }
        catch (JsonException) { return false; }
    }
}