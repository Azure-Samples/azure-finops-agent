using System.Security.Cryptography;
using System.Text.Json;
using AzureFinOps.Dashboard.Observability;
using GitHub.Copilot;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Endpoints for the user identity (anonymous-by-default), logout, and the full
/// Microsoft Entra ID multi-tenant OAuth flow with incremental consent + admin-consent
/// chained acquisition.
/// </summary>
public static class MicrosoftAuthEndpoints
{
    /// <summary>Generates a cryptographically random hex string for OAuth `state` and PKCE values.</summary>
    private static string CryptoRandomHex(int byteLen) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteLen)).ToLowerInvariant();

    /// <summary>RFC 7636 PKCE code_challenge from a code_verifier (SHA-256, base64url).</summary>
    private static string PkceChallenge(string verifier)
    {
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    public static void MapMicrosoftAuthEndpoints(
        this IEndpointRouteBuilder app,
        MicrosoftOAuthOptions options,
        EntraClientCredentials credentials,
        IdTokenValidator idTokenValidator,
        AiTelemetry telemetry,
        PersistentIdentity persistentIdentity,
        ILogger logger)
    {
        // Anonymous-or-Azure-enriched user identity
        app.MapGet("/auth/me", (HttpContext ctx) =>
        {
            var userJson = ctx.Session.GetString("user");
            if (userJson is null)
                return Results.Json(new { id = 0, login = "anonymous" });

            var userObj = JsonSerializer.Deserialize<JsonElement>(userJson);

            var azureUserJson = ctx.Session.GetString("azure_user");
            string? name = null, email = null;
            if (azureUserJson is not null)
            {
                var azureUser = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                if (azureUser.TryGetProperty("name", out var n)) name = n.GetString();
                if (azureUser.TryGetProperty("email", out var e)) email = e.GetString();
            }

            return Results.Json(new
            {
                id = userObj.GetProperty("id").GetInt64(),
                login = userObj.GetProperty("login").GetString(),
                name = name ?? (userObj.TryGetProperty("name", out var n2) ? n2.GetString() : null),
                email = email ?? (userObj.TryGetProperty("email", out var e2) ? e2.GetString() : null),
            });
        });

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            var userJson = ctx.Session.GetString("user");
            string? oid = null;
            var azureUserJson = ctx.Session.GetString("azure_user");
            if (azureUserJson is not null)
            {
                try
                {
                    var au = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                    if (au.TryGetProperty("objectId", out var oidProp)) oid = oidProp.GetString();
                }
                catch { }
            }
            if (userJson is not null)
            {
                var u = JsonSerializer.Deserialize<JsonElement>(userJson);
                var uid = u.GetProperty("id").GetInt64();
                var owned = telemetry.LiveSessions.Where(kv => kv.Value.UserId == uid).Select(kv => kv.Key).ToList();
                foreach (var sid in owned)
                {
                    if (telemetry.LiveSessions.TryRemove(sid, out var live))
                    {
                        telemetry.ActiveSessions.Add(-1);
                        try { await live.Session.DisposeAsync(); } catch { }
                    }
                }
                telemetry.CurrentSessionId.TryRemove(uid, out _);
                telemetry.UserTokens.TryRemove(uid, out _);
                telemetry.UserTools.TryRemove(uid, out _);
            }
            ctx.Session.Clear();
            persistentIdentity.Clear(ctx, oid);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/auth/microsoft", (HttpContext ctx) =>
        {
            if (!options.IsConfigured)
                return Results.Problem("Microsoft OAuth is not configured");

            var state = CryptoRandomHex(16);
            ctx.Session.SetString("ms_oauth_state", state);

            // PKCE — defends against authorization-code interception even though
            // we're a confidential client. Microsoft now recommends it for web apps too.
            var codeVerifier = CryptoRandomHex(48);
            ctx.Session.SetString("pkce_verifier", codeVerifier);
            var codeChallenge = PkceChallenge(codeVerifier);

            // OIDC nonce — bound into the id_token by the IdP and verified on callback
            // to defeat token replay between sessions.
            var nonce = CryptoRandomHex(16);
            ctx.Session.SetString("oidc_nonce", nonce);

            // Allowlist-validate the requested tier — reject anything outside the
            // known set so we never construct an /authorize URL with attacker-supplied scopes.
            var tier = MicrosoftOAuthOptions.NormalizeTier(ctx.Request.Query["tier"].ToString());

            // Post-admin-consent silent chain: walk every remaining add-on tier
            if (ctx.Request.Query["postadmin"].ToString() == "1")
            {
                var chain = new List<string> { "chargeback", "loganalytics", "storage" };
                ctx.Session.SetString("auth_chain", string.Join(",", chain));
                ctx.Session.SetString("auth_silent", "1");
            }

            ctx.Session.SetString("auth_tier", tier);

            var tenantParam = ctx.Request.Query["tenant"].ToString().Trim();
            if (!string.IsNullOrEmpty(tenantParam))
            {
                if (!MicrosoftOAuthOptions.IsValidTenantId(tenantParam))
                {
                    logger.LogWarning("Rejected invalid tenant query param: {Tenant}", tenantParam);
                    return Results.BadRequest("Invalid tenant identifier");
                }
                ctx.Session.SetString("auth_tenant", tenantParam);
            }
            var effectiveTenant = ctx.Session.GetString("auth_tenant") ?? options.TenantId;
            if (!MicrosoftOAuthOptions.IsValidTenantId(effectiveTenant))
                effectiveTenant = options.TenantId;

            var redirectUri = $"{MicrosoftOAuthOptions.NormalizeCallbackHost(ctx)}/auth/microsoft/callback";
            var scope = string.Join(" ", ["openid", "profile", "email", "offline_access", .. MicrosoftOAuthOptions.GetScopesForTier(tier)]);

            var forceConsent = ctx.Session.GetString("force_consent") == "1";
            ctx.Session.Remove("force_consent");
            var silentChain = ctx.Session.GetString("auth_silent") == "1";
            string promptType = silentChain ? "none"
                : (tier != "base" || forceConsent) ? "consent"
                : "select_account";

            var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(effectiveTenant)}/oauth2/v2.0/authorize" +
                      $"?client_id={Uri.EscapeDataString(options.ClientId)}" +
                      $"&response_type=code" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&scope={Uri.EscapeDataString(scope)}" +
                      $"&state={state}" +
                      $"&nonce={nonce}" +
                      $"&response_mode=query" +
                      $"&prompt={promptType}" +
                      $"&code_challenge={codeChallenge}" +
                      $"&code_challenge_method=S256";

            if (promptType == "none")
            {
                var azureUserJson = ctx.Session.GetString("azure_user");
                if (!string.IsNullOrEmpty(azureUserJson))
                {
                    try
                    {
                        var u = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                        if (u.TryGetProperty("email", out var emailProp) && emailProp.ValueKind == JsonValueKind.String)
                            url += $"&login_hint={Uri.EscapeDataString(emailProp.GetString()!)}";
                    }
                    catch { }
                }
            }

            logger.LogInformation("Microsoft OAuth redirect: tier={Tier} prompt={Prompt} tenant={Tenant} from {Host}",
                tier, promptType, effectiveTenant, ctx.Request.Host);
            return Results.Redirect(url);
        });

        app.MapGet("/auth/microsoft/adminconsent", (HttpContext ctx) =>
        {
            if (!options.IsConfigured)
                return Results.Problem("Microsoft OAuth is not configured");

            var state = CryptoRandomHex(16);
            ctx.Session.SetString("ms_oauth_state", state);
            ctx.Session.SetString("auth_tier", "adminconsent");

            var tenantParam = ctx.Request.Query["tenant"].ToString().Trim();
            if (!string.IsNullOrEmpty(tenantParam))
                ctx.Session.SetString("auth_tenant", tenantParam);
            var effectiveTenant = ctx.Session.GetString("auth_tenant") ?? options.TenantId;
            if (effectiveTenant == "common") effectiveTenant = "organizations";

            var redirectUri = $"{MicrosoftOAuthOptions.NormalizeCallbackHost(ctx)}/auth/microsoft/adminconsent/callback";
            var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(effectiveTenant)}/v2.0/adminconsent" +
                      $"?client_id={Uri.EscapeDataString(options.ClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&state={state}" +
                      $"&scope=https://graph.microsoft.com/.default";

            logger.LogInformation("Admin consent redirect: tenant={Tenant} from {Host}", effectiveTenant, ctx.Request.Host);
            return Results.Redirect(url);
        });

        app.MapGet("/auth/microsoft/adminconsent/callback", (HttpContext ctx) =>
        {
            var state = ctx.Request.Query["state"].ToString();
            if (state != ctx.Session.GetString("ms_oauth_state"))
            {
                logger.LogWarning("Admin consent state mismatch — possible CSRF");
                return Results.StatusCode(403);
            }
            ctx.Session.Remove("ms_oauth_state");

            var error = ctx.Request.Query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
            {
                var desc = ctx.Request.Query["error_description"].ToString();
                logger.LogWarning("Admin consent failed: {Error} — {Desc}", error, desc);
                return Results.Redirect("/?azure_error=" + Uri.EscapeDataString(error));
            }

            var grantedTenant = ctx.Request.Query["tenant"].ToString();
            logger.LogInformation("Admin consent granted for tenant={Tenant}", grantedTenant);
            if (!string.IsNullOrEmpty(grantedTenant))
                ctx.Session.SetString("auth_tenant", grantedTenant);
            return Results.Redirect("/?admin_consent=ok&tenant=" + Uri.EscapeDataString(grantedTenant));
        });

        app.MapGet("/auth/microsoft/callback", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
        {
            try
            {
                var code = ctx.Request.Query["code"].ToString();
                var state = ctx.Request.Query["state"].ToString();
                var error = ctx.Request.Query["error"].ToString();

                if (!string.IsNullOrEmpty(error))
                {
                    var errorDesc = ctx.Request.Query["error_description"].ToString();
                    logger.LogWarning("Microsoft OAuth error: {Error} — {Description}", error, errorDesc);
                    return Results.Redirect("/?azure_error=" + Uri.EscapeDataString(error));
                }

                if (state != ctx.Session.GetString("ms_oauth_state"))
                {
                    logger.LogWarning("Microsoft OAuth state mismatch — possible CSRF attempt");
                    return Results.StatusCode(403);
                }

                ctx.Session.Remove("ms_oauth_state");

                // Named client with 30 s overall timeout. IPv4-only transport is
                // already applied by ConfigureHttpClientDefaults in Program.cs.
                var http = httpFactory.CreateClient("entra-token");
                var redirectUri = $"{MicrosoftOAuthOptions.NormalizeCallbackHost(ctx)}/auth/microsoft/callback";
                var effectiveTenant = ctx.Session.GetString("auth_tenant") ?? options.TenantId;

                using var tokenReq = new HttpRequestMessage(HttpMethod.Post,
                    $"https://login.microsoftonline.com/{Uri.EscapeDataString(effectiveTenant)}/oauth2/v2.0/token");

                var authTier = ctx.Session.GetString("auth_tier") ?? "base";
                var tokenExchangeScope = string.Join(" ", ["openid", "profile", "email", "offline_access", .. MicrosoftOAuthOptions.GetScopesForTier(authTier)]);

                var pkceVerifier = ctx.Session.GetString("pkce_verifier");
                ctx.Session.Remove("pkce_verifier");

                var tokenForm = new Dictionary<string, string>
                {
                    ["client_id"] = options.ClientId,
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri,
                    ["grant_type"] = "authorization_code",
                    ["scope"] = tokenExchangeScope
                };
                if (!string.IsNullOrEmpty(pkceVerifier))
                    tokenForm["code_verifier"] = pkceVerifier;
                // Adds either client_secret OR client_assertion (federated MI). Prefer the latter.
                await credentials.AddCredentialFieldsAsync(tokenForm);

                tokenReq.Content = new FormUrlEncodedContent(tokenForm);

                var tokenRes = await http.SendAsync(tokenReq);
                var tokenBody = await tokenRes.Content.ReadAsStringAsync();

                if (!tokenRes.IsSuccessStatusCode)
                {
                    logger.LogError("Microsoft token exchange failed: status={Status} body={Body}", (int)tokenRes.StatusCode, tokenBody);
                    return Results.Redirect("/?azure_error=token_exchange_failed");
                }

                var tokenJson = JsonSerializer.Deserialize<JsonElement>(tokenBody);

                if (!tokenJson.TryGetProperty("access_token", out var atProp))
                {
                    logger.LogError("No access_token in Microsoft response");
                    return Results.Redirect("/?azure_error=no_access_token");
                }

                var accessToken = atProp.GetString()!;
                var refreshToken = tokenJson.TryGetProperty("refresh_token", out var rtProp) ? rtProp.GetString() : null;
                var expiresIn = tokenJson.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;

                if (authTier == "licenses" || authTier == "chargeback")
                {
                    ctx.Session.SetString("graph_token", accessToken);
                    ctx.Session.SetString("graph_token_expiry", DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToString("o"));
                    var existingTier = ctx.Session.GetString("graph_tier") ?? "";
                    if (!existingTier.Contains(authTier))
                        ctx.Session.SetString("graph_tier", string.IsNullOrEmpty(existingTier) ? authTier : $"{existingTier},{authTier}");
                }
                else if (authTier == "loganalytics")
                {
                    ctx.Session.SetString("loganalytics_token", accessToken);
                    ctx.Session.SetString("loganalytics_token_expiry", DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToString("o"));
                }
                else if (authTier == "storage")
                {
                    ctx.Session.SetString("storage_token", accessToken);
                    ctx.Session.SetString("storage_token_expiry", DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToString("o"));
                }
                else
                {
                    ctx.Session.SetString("azure_token", accessToken);
                    ctx.Session.SetString("azure_token_expiry", DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToString("o"));
                }

                if (refreshToken is not null)
                    ctx.Session.SetString("azure_refresh_token", refreshToken);

                if (tokenJson.TryGetProperty("id_token", out var idTokenProp))
                {
                    var idToken = idTokenProp.GetString()!;
                    var expectedNonce = ctx.Session.GetString("oidc_nonce") ?? "";
                    ctx.Session.Remove("oidc_nonce");
                    var validated = await idTokenValidator.ValidateAsync(idToken, expectedNonce);
                    if (validated is null)
                    {
                        logger.LogWarning("id_token failed validation — aborting login");
                        return Results.Redirect("/?azure_error=id_token_invalid");
                    }
                    var azureUser = new Dictionary<string, string?>
                    {
                        // oid+tid is the only stable, attacker-resistant identity in a multi-tenant
                        // app. email/preferred_username are display-only (defends against nOAuth).
                        ["tenantId"] = validated.TenantId,
                        ["objectId"] = validated.ObjectId,
                        ["name"] = validated.Name,
                        ["email"] = validated.Email ?? validated.PreferredUsername,
                    };
                    ctx.Session.SetString("azure_user", JsonSerializer.Serialize(azureUser));

                    // Promote the random anonymous userId to a deterministic OID-derived id,
                    // migrating any in-memory per-user state so the current chat doesn't get
                    // orphaned mid-conversation.
                    var oid = validated.ObjectId;
                    if (!string.IsNullOrEmpty(oid))
                    {
                        var newUserId = PersistentIdentity.DeriveUserId(oid);
                        long? oldUserId = null;
                        var existingUserJson = ctx.Session.GetString("user");
                        if (existingUserJson is not null)
                        {
                            try
                            {
                                var u = JsonSerializer.Deserialize<JsonElement>(existingUserJson);
                                oldUserId = u.GetProperty("id").GetInt64();
                            }
                            catch { }
                        }
                        if (oldUserId.HasValue && oldUserId.Value != newUserId)
                        {
                            // Re-key per-user dicts. Last-write wins is fine: a single user can't
                            // be in flight under two ids on the same browser session.
                            if (telemetry.UserTokens.TryRemove(oldUserId.Value, out var t)) telemetry.UserTokens[newUserId] = t;
                            if (telemetry.UserTools.TryRemove(oldUserId.Value, out var tools)) telemetry.UserTools[newUserId] = tools;
                            if (telemetry.CurrentSessionId.TryRemove(oldUserId.Value, out var sid)) telemetry.CurrentSessionId[newUserId] = sid;
                            // LiveSessions has init-only UserId; not migrated. Any in-flight CLI
                            // session under the anon id will be cleaned up by the idle-timeout
                            // sweep (30 min) and the next prompt creates a fresh one under newUserId.
                        }
                        ctx.Session.SetString("user", JsonSerializer.Serialize(new
                        {
                            id = newUserId,
                            login = $"user-{newUserId & 0xFFFF:X4}",
                            name = validated.Name,
                            avatar = (string?)null,
                            email = validated.Email ?? validated.PreferredUsername,
                        }));

                        // Persist identity + the rotating refresh_token to /home so the user
                        // doesn't have to re-auth after a container restart. On non-base tier
                        // callbacks Entra still returns a refresh_token (because we always
                        // request offline_access), so this also keeps the persisted record's
                        // GraphTier in sync as the user adds add-on consents incrementally.
                        if (!string.IsNullOrEmpty(refreshToken))
                        {
                            await persistentIdentity.SaveIdentityAsync(ctx, new IdentityRecord
                            {
                                Oid = oid,
                                TenantId = validated.TenantId ?? "",
                                UserId = newUserId,
                                Name = validated.Name,
                                Email = validated.Email ?? validated.PreferredUsername,
                                RefreshToken = refreshToken,
                                GraphTier = ctx.Session.GetString("graph_tier"),
                            });
                        }
                        else
                        {
                            // Edge case: re-consent without a fresh refresh_token. Update only
                            // the GraphTier so post-restart hydration still reflects the new
                            // add-on without clobbering the existing refresh token.
                            await persistentIdentity.UpdateGraphTierAsync(oid, ctx.Session.GetString("graph_tier"));
                        }
                    }
                }

                logger.LogInformation("Microsoft OAuth login successful, tier={Tier}", authTier);

                var pendingChain = ctx.Session.GetString("auth_chain");
                if (!string.IsNullOrEmpty(pendingChain))
                {
                    var parts = pendingChain.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        var next = parts[0];
                        var rest = string.Join(",", parts.Skip(1));
                        ctx.Session.SetString("auth_chain", rest);
                        // Defensive: only chain to known tiers. Anything unexpected
                        // means session tampering — drop the chain and finish normally.
                        if (!MicrosoftOAuthOptions.ValidTiers.Contains(next))
                        {
                            logger.LogWarning("Dropped auth_chain entry with invalid tier: {Tier}", next);
                            ctx.Session.Remove("auth_chain");
                        }
                        else
                        {
                            return Results.Redirect($"/auth/microsoft?tier={Uri.EscapeDataString(next)}");
                        }
                    }
                    ctx.Session.Remove("auth_chain");
                }

                ctx.Session.Remove("auth_silent");

                return Results.Redirect("/");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Microsoft OAuth callback failed");
                return Results.Redirect("/?azure_error=callback_failed");
            }
        });
    }
}
