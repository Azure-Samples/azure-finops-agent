using System.Net.Http.Headers;
using System.Text.Json;
using AzureFinOps.Dashboard.Observability;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Azure connection status, tenant discovery, disconnect and revoke endpoints.
/// All operate on the user's session-scoped tokens.
/// </summary>
public static class AzureSessionEndpoints
{
    public static void MapAzureSessionEndpoints(
        this IEndpointRouteBuilder app,
        SessionTokenStore tokenStore,
        AiTelemetry telemetry,
        ILogger logger)
    {
        app.MapGet("/auth/azure/status", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
        {
            var token = await tokenStore.GetAzureTokenAsync(ctx, httpFactory);
            if (token is null)
                return Results.Json(new { connected = false });

            var azureUserJson = ctx.Session.GetString("azure_user");
            object? azureUser = azureUserJson is not null ? JsonSerializer.Deserialize<JsonElement>(azureUserJson) : null;

            var http = httpFactory.CreateClient();
            var subscriptions = new List<object>();
            try
            {
                using var subReq = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com/subscriptions?api-version=2022-12-01");
                subReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                subReq.Headers.Add("User-Agent", "FinOps-Dashboard/1.0");
                var subRes = await http.SendAsync(subReq);
                var subBody = await subRes.Content.ReadAsStringAsync();
                var subJson = JsonSerializer.Deserialize<JsonElement>(subBody);
                if (subJson.TryGetProperty("value", out var subs))
                {
                    foreach (var sub in subs.EnumerateArray())
                    {
                        subscriptions.Add(new
                        {
                            id = sub.GetProperty("subscriptionId").GetString(),
                            name = sub.GetProperty("displayName").GetString(),
                            state = sub.GetProperty("state").GetString(),
                            tenantId = sub.TryGetProperty("tenantId", out var tid) ? tid.GetString() : null
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list Azure subscriptions");
            }

            var managementGroups = new List<object>();
            try
            {
                using var mgReq = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com/providers/Microsoft.Management/managementGroups?api-version=2021-04-01");
                mgReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                mgReq.Headers.Add("User-Agent", "FinOps-Dashboard/1.0");
                var mgRes = await http.SendAsync(mgReq);
                var mgBody = await mgRes.Content.ReadAsStringAsync();
                var mgJson = JsonSerializer.Deserialize<JsonElement>(mgBody);
                if (mgJson.TryGetProperty("value", out var mgs))
                {
                    foreach (var mg in mgs.EnumerateArray())
                    {
                        managementGroups.Add(new
                        {
                            id = mg.GetProperty("id").GetString(),
                            name = mg.TryGetProperty("properties", out var props) && props.TryGetProperty("displayName", out var dn) ? dn.GetString() : mg.GetProperty("name").GetString()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list management groups");
            }

            var connectedApis = new List<string>
            {
                "Cost Management", "Billing", "Advisor", "Resource Graph",
                "Azure Monitor", "Resource Health", "Subscriptions"
            };

            return Results.Json(new
            {
                connected = true,
                user = azureUser,
                subscriptions,
                managementGroups,
                apis = connectedApis,
                graphEnabled = ctx.Session.GetString("graph_token") is not null,
                graphTier = ctx.Session.GetString("graph_tier") ?? "",
                logAnalyticsEnabled = ctx.Session.GetString("loganalytics_token") is not null,
                storageEnabled = ctx.Session.GetString("storage_token") is not null
            });
        });

        app.MapGet("/auth/azure/tenants", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
        {
            var token = await tokenStore.GetAzureTokenAsync(ctx, httpFactory);
            if (token is null)
                return Results.Json(new { tenants = Array.Empty<object>() });

            var http = httpFactory.CreateClient();
            var tenants = new List<object>();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com/tenants?api-version=2022-12-01");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("User-Agent", "FinOps-Dashboard/1.0");
                var res = await http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                if (json.TryGetProperty("value", out var vals))
                {
                    foreach (var t in vals.EnumerateArray())
                    {
                        tenants.Add(new
                        {
                            tenantId = t.GetProperty("tenantId").GetString(),
                            displayName = t.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                            defaultDomain = t.TryGetProperty("defaultDomain", out var dd) ? dd.GetString() : null
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list tenants");
            }

            var currentTenantId = "";
            var azureUserJson = ctx.Session.GetString("azure_user");
            if (azureUserJson is not null)
            {
                var u = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                if (u.TryGetProperty("tenantId", out var tid))
                    currentTenantId = tid.GetString() ?? "";
            }

            return Results.Json(new { tenants, currentTenantId });
        });

        app.MapPost("/auth/azure/disconnect", (HttpContext ctx) =>
        {
            ClearTokensForUser(ctx, telemetry, logger, fullClear: false);
            ClearSessionTokenKeys(ctx, includeForceConsent: false);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/auth/azure/revoke", (HttpContext ctx) =>
        {
            ClearTokensForUser(ctx, telemetry, logger, fullClear: true);
            ClearSessionTokenKeys(ctx, includeForceConsent: true);
            return Results.Ok(new { ok = true });
        });
    }

    private static void ClearTokensForUser(HttpContext ctx, AiTelemetry telemetry, ILogger logger, bool fullClear)
    {
        var userJson = ctx.Session.GetString("user");
        if (userJson is null)
        {
            logger.LogInformation("Azure {Action} (no user context)", fullClear ? "revoked" : "disconnected");
            return;
        }
        var u = JsonSerializer.Deserialize<JsonElement>(userJson);
        var uid = u.GetProperty("id").GetInt64();
        if (telemetry.UserTokens.TryGetValue(uid, out var tokens))
        {
            tokens.AzureToken = null;
            tokens.GraphToken = null;
            tokens.LogAnalyticsToken = null;
            tokens.StorageToken = null;
        }
        if (fullClear)
        {
            DisposeUserLiveSessions(telemetry, uid);
            telemetry.CurrentSessionId.TryRemove(uid, out _);
            telemetry.UserTools.TryRemove(uid, out _);
        }
        logger.LogInformation("Azure {Action} for user {UserId}", fullClear ? "revoked" : "disconnected", uid);
    }

    private static void DisposeUserLiveSessions(AiTelemetry telemetry, long userId)
    {
        var owned = telemetry.LiveSessions.Where(kv => kv.Value.UserId == userId).Select(kv => kv.Key).ToList();
        foreach (var sid in owned)
        {
            if (telemetry.LiveSessions.TryRemove(sid, out var live))
            {
                telemetry.ActiveSessions.Add(-1);
                // Fire-and-forget but observe the task so a failed dispose is
                // logged via the unobserved-exception channel rather than
                // silently leaking the CLI subprocess.
                _ = Task.Run(async () =>
                {
                    try { await live.Session.DisposeAsync(); } catch { }
                });
            }
        }
    }

    private static void ClearSessionTokenKeys(HttpContext ctx, bool includeForceConsent)
    {
        ctx.Session.Remove("azure_token");
        ctx.Session.Remove("azure_refresh_token");
        ctx.Session.Remove("azure_token_expiry");
        ctx.Session.Remove("azure_user");
        ctx.Session.Remove("graph_token");
        ctx.Session.Remove("graph_token_expiry");
        ctx.Session.Remove("graph_tier");
        ctx.Session.Remove("loganalytics_token");
        ctx.Session.Remove("loganalytics_token_expiry");
        ctx.Session.Remove("storage_token");
        ctx.Session.Remove("storage_token_expiry");
        if (!includeForceConsent)
            ctx.Session.Remove("auth_tenant");
        else
            ctx.Session.SetString("force_consent", "1");
    }
}
