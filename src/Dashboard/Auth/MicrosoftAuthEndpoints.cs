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

    /// <summary>Appends a consented add-on tier to the session's consent list
    /// (session key <c>graph_tier</c>, persisted via <c>IdentityRecord.GraphTier</c>).
    /// Tracked for ALL add-on tiers — licenses, chargeback, loganalytics, storage —
    /// so <see cref="SessionTokenStore"/> can skip refresh-token exchanges for
    /// scopes the user never consented to (each is a guaranteed HTTP 400).</summary>
    private static void AppendConsentTier(HttpContext ctx, string tier)
    {
        var existing = ctx.Session.GetString("graph_tier") ?? "";
        if (!existing.Contains(tier, StringComparison.OrdinalIgnoreCase))
            ctx.Session.SetString("graph_tier", string.IsNullOrEmpty(existing) ? tier : $"{existing},{tier}");
    }

    private static string? CurrentAzureOid(HttpContext ctx)
    {
        var json = ctx.Session.GetString("azure_user");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var user = JsonSerializer.Deserialize<JsonElement>(json);
            return user.TryGetProperty("objectId", out var oid) ? oid.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ClearAuthChain(HttpContext ctx)
    {
        ctx.Session.Remove("auth_chain");
        ctx.Session.Remove("auth_chain_oid");
        ctx.Session.Remove("auth_silent");
    }

    private static void ClearTierToken(HttpContext ctx, string tier)
    {
        var prefix = tier switch
        {
            "licenses" or "chargeback" => "graph",
            "loganalytics" => "loganalytics",
            "storage" => "storage",
            _ => "azure"
        };
        ctx.Session.Remove($"{prefix}_token");
        ctx.Session.Remove($"{prefix}_token_expiry");
        RemoveConsentTier(ctx, tier);
    }

    /// <summary>Undoes <see cref="AppendConsentTier"/> for a hop that failed after
    /// the token landed. Leaving it recorded would make `tier=all` skip that tier
    /// forever and poison the account-switch rebuild.</summary>
    private static void RemoveConsentTier(HttpContext ctx, string tier)
    {
        var existing = ctx.Session.GetString("graph_tier");
        if (string.IsNullOrEmpty(existing)) return;
        var remaining = existing
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.Equals(tier, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remaining.Length == 0) ctx.Session.Remove("graph_tier");
        else ctx.Session.SetString("graph_tier", string.Join(",", remaining));
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

            var continuingChain = ctx.Request.Query["chain"].ToString() == "1"
                && !string.IsNullOrWhiteSpace(ctx.Session.GetString("auth_chain"));
            if (!continuingChain) ClearAuthChain(ctx);

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

            // User-scoped "grant all remaining" flow. Entra cannot combine
            // delegated scopes from Graph, Log Analytics, Storage, and ARM in
            // one authorization request, so walk each not-yet-consented
            // resource tier in sequence. Every hop uses prompt=consent and its
            // own narrowly scoped screen; no tenant-admin grant is involved.
            if (tier == "all")
            {
                var consented = (ctx.Session.GetString("graph_tier") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var remaining = MicrosoftOAuthOptions.AddOnTiers
                    .Where(t => !consented.Contains(t))
                    .ToArray();
                if (remaining.Length == 0)
                {
                    ClearAuthChain(ctx);
                    return Results.Redirect("/");
                }
                tier = remaining[0];
                if (remaining.Length > 1)
                    ctx.Session.SetString("auth_chain", string.Join(",", remaining.Skip(1)));
                else
                    ctx.Session.Remove("auth_chain");
                ctx.Session.SetString("auth_chain_oid", CurrentAzureOid(ctx) ?? "");
                ctx.Session.Remove("auth_silent");
            }

            // Post-admin-consent silent chain: walk every remaining add-on tier
            if (ctx.Request.Query["postadmin"].ToString() == "1")
            {
                var chain = new List<string> { "chargeback", "loganalytics", "storage" };
                ctx.Session.SetString("auth_chain", string.Join(",", chain));
                ctx.Session.SetString("auth_chain_oid", CurrentAzureOid(ctx) ?? "");
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
                    ctx.Session.Remove("ms_oauth_state");
                    ctx.Session.Remove("pkce_verifier");
                    ctx.Session.Remove("oidc_nonce");
                    ClearAuthChain(ctx);
                    return Results.Redirect("/?azure_error=" + Uri.EscapeDataString(error));
                }

                if (state != ctx.Session.GetString("ms_oauth_state"))
                {
                    logger.LogWarning("Microsoft OAuth state mismatch — possible CSRF attempt");
                    ctx.Session.Remove("pkce_verifier");
                    ctx.Session.Remove("oidc_nonce");
                    ClearAuthChain(ctx);
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
                    logger.LogError("Microsoft token exchange failed: status={Status}", (int)tokenRes.StatusCode);
                    ctx.Session.Remove("oidc_nonce");
                    ClearAuthChain(ctx);
                    return Results.Redirect("/?azure_error=token_exchange_failed");
                }

                var tokenJson = JsonSerializer.Deserialize<JsonElement>(tokenBody);

                if (!tokenJson.TryGetProperty("access_token", out var atProp))
                {
                    logger.LogError("No access_token in Microsoft response");
                    ctx.Session.Remove("oidc_nonce");
                    ClearAuthChain(ctx);
                    return Results.Redirect("/?azure_error=no_access_token");
                }

                var accessToken = atProp.GetString()!;
                var refreshToken = tokenJson.TryGetProperty("refresh_token", out var rtProp) ? rtProp.GetString() : null;
                var expiresIn = tokenJson.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;

                if (authTier == "licenses" || authTier == "chargeback")
                {
                    ctx.Session.SetString("graph_token", accessToken);
                    ctx.Session.SetString("graph_token_expiry", DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToString("o"));
                    AppendConsentTier(ctx, authTier);
                    ctx.Session.Remove("graph_token_unavailable_until");
                }
                else if (authTier == "loganalytics")
                {
                    ctx.Session.SetString("loganalytics_token", accessToken);
                    ctx.Session.SetString("loganalytics_token_expiry", DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToString("o"));
                    AppendConsentTier(ctx, authTier);
                    ctx.Session.Remove("loganalytics_token_unavailable_until");
                }
                else if (authTier == "storage")
                {
                    ctx.Session.SetString("storage_token", accessToken);
                    ctx.Session.SetString("storage_token_expiry", DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToString("o"));
                    AppendConsentTier(ctx, authTier);
                    ctx.Session.Remove("storage_token_unavailable_until");
                }
                else
                {
                    ctx.Session.SetString("azure_token", accessToken);
                    ctx.Session.SetString("azure_token_expiry", DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToString("o"));
                }

                if (!tokenJson.TryGetProperty("id_token", out var idTokenProp)
                    || idTokenProp.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(idTokenProp.GetString()))
                {
                    ClearTierToken(ctx, authTier);
                    ClearAuthChain(ctx);
                    ctx.Session.Remove("oidc_nonce");
                    logger.LogWarning("No id_token in Microsoft response — aborting login");
                    return Results.Redirect("/?azure_error=no_id_token");
                }

                {
                    var idToken = idTokenProp.GetString()!;
                    var expectedNonce = ctx.Session.GetString("oidc_nonce") ?? "";
                    ctx.Session.Remove("oidc_nonce");
                    var validated = await idTokenValidator.ValidateAsync(idToken, expectedNonce);
                    if (validated is null)
                    {
                        ClearTierToken(ctx, authTier);
                        logger.LogWarning("id_token failed validation — aborting login");
                        ClearAuthChain(ctx);
                        return Results.Redirect("/?azure_error=id_token_invalid");
                    }

                    if (refreshToken is not null)
                        ctx.Session.SetString("azure_refresh_token", refreshToken);

                    // Capture the PREVIOUS Entra identity (if any) before overwriting
                    // azure_user — needed below to detect an account switch on this
                    // browser session and isolate the two accounts' state.
                    string? previousOid = null;
                    var prevAzureUserJson = ctx.Session.GetString("azure_user");
                    if (prevAzureUserJson is not null)
                    {
                        try
                        {
                            var prev = JsonSerializer.Deserialize<JsonElement>(prevAzureUserJson);
                            if (prev.TryGetProperty("objectId", out var prevOidProp))
                                previousOid = prevOidProp.GetString();
                        }
                        catch { }
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
                    ctx.Session.Remove("azure_scope_context");
                    ctx.Session.SetString("azure_user", JsonSerializer.Serialize(azureUser));

                    // Promote the random anonymous userId to a deterministic OID-derived id,
                    // migrating any in-memory per-user state so the current chat doesn't get
                    // orphaned mid-conversation.
                    var oid = validated.ObjectId;
                    if (!string.IsNullOrEmpty(oid))
                    {
                        // A DIFFERENT Entra account signed in on a browser session that
                        // already belonged to another Entra account (e.g. the user
                        // switched tenant/account via "Connect Azure"). The two accounts
                        // must be fully isolated: without this, account B inherited
                        // account A's Graph/Log-Analytics/Storage tokens, consent tiers,
                        // and (via the migration below) A's live conversation + ARM token
                        // — a cross-tenant data leak observed in production.
                        var accountSwitched = previousOid is not null
                            && !string.Equals(previousOid, oid, StringComparison.OrdinalIgnoreCase);
                        if (accountSwitched)
                        {
                            logger.LogInformation("Entra account switch detected (oid {PrevOid} → {NewOid}); isolating session state", previousOid, oid);
                            // Purge every resource token EXCEPT the ones this callback
                            // just minted for the new account (keyed by authTier).
                            string[] keep = authTier switch
                            {
                                "licenses" or "chargeback" => ["graph_token", "graph_token_expiry"],
                                "loganalytics" => ["loganalytics_token", "loganalytics_token_expiry"],
                                "storage" => ["storage_token", "storage_token_expiry"],
                                _ => ["azure_token", "azure_token_expiry"],
                            };
                            string[] allTokenKeys =
                            [
                                "azure_token", "azure_token_expiry",
                                "graph_token", "graph_token_expiry",
                                "loganalytics_token", "loganalytics_token_expiry",
                                "storage_token", "storage_token_expiry",
                                "graph_token_unavailable_until", "loganalytics_token_unavailable_until", "storage_token_unavailable_until",
                            ];
                            foreach (var key in allTokenKeys.Except(keep))
                                ctx.Session.Remove(key);
                            // Consent tiers belong to the previous account — reset to just
                            // what this callback granted (otherwise A's tiers get persisted
                            // into B's identity record below).
                            if (authTier is "licenses" or "chargeback" or "loganalytics" or "storage")
                                ctx.Session.SetString("graph_tier", authTier);
                            else
                                ctx.Session.Remove("graph_tier");
                            // Without a fresh refresh token, the previous account's RT must
                            // not survive — it would mint tokens for the WRONG account.
                            if (refreshToken is null)
                                ctx.Session.Remove("azure_refresh_token");
                        }

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
                        if (oldUserId.HasValue && oldUserId.Value != newUserId && !accountSwitched)
                        {
                            // Anonymous → Entra promotion ONLY. Re-key per-user dicts so the
                            // current chat doesn't get orphaned mid-conversation. Last-write
                            // wins is fine: a single user can't be in flight under two ids on
                            // the same browser session. On an Entra→Entra ACCOUNT SWITCH this
                            // migration must NOT run — it would hand account A's tokens,
                            // tools, and active conversation to account B.
                            // Tool closures capture the token bag, whose UserId also
                            // determines per-user persistence paths (scores, ledger,
                            // uploads). Never re-key those closures to another user:
                            // drop them and let the next request create a fresh bag
                            // for the stable Entra-derived id.
                            if (telemetry.UserTokens.TryRemove(oldUserId.Value, out var oldTokens))
                                oldTokens.RefreshLock.Dispose();
                            telemetry.UserTools.TryRemove(oldUserId.Value, out _);
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
                            if (accountSwitched)
                            {
                                // SaveIdentityAsync (which rewrites the finops_id cookie) did not
                                // run — the cookie still points at the PREVIOUS account's OID and
                                // would resurrect that identity on the next hydration. Point it at
                                // the new account when it has a persisted identity, else drop it.
                                if (persistentIdentity.LoadByOid(oid) is not null)
                                    persistentIdentity.SetIdentityCookie(ctx, oid);
                                else
                                    persistentIdentity.Clear(ctx, null);
                            }
                        }

                        var chainOwner = ctx.Session.GetString("auth_chain_oid");
                        if (chainOwner is not null
                            && !string.Equals(chainOwner, oid, StringComparison.OrdinalIgnoreCase))
                        {
                            // The account selected on an intermediate consent
                            // screen changed. Rebuild the remaining sequence from
                            // the tiers actually recorded for the new identity;
                            // never carry account A's omissions into account B.
                            var consentedForNewAccount = (ctx.Session.GetString("graph_tier") ?? "")
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            var remainingForNewAccount = MicrosoftOAuthOptions.AddOnTiers
                                .Where(t => !consentedForNewAccount.Contains(t))
                                .ToArray();
                            if (remainingForNewAccount.Length > 0)
                                ctx.Session.SetString("auth_chain", string.Join(",", remainingForNewAccount));
                            else
                                ctx.Session.Remove("auth_chain");
                            ctx.Session.SetString("auth_chain_oid", oid);
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
                        if (!MicrosoftOAuthOptions.AddOnTiers.Contains(next, StringComparer.OrdinalIgnoreCase))
                        {
                            logger.LogWarning("Dropped auth_chain entry with invalid tier: {Tier}", next);
                            ctx.Session.Remove("auth_chain");
                        }
                        else
                        {
                            return Results.Redirect($"/auth/microsoft?tier={Uri.EscapeDataString(next)}&chain=1");
                        }
                    }
                    ctx.Session.Remove("auth_chain");
                }

                ClearAuthChain(ctx);

                return Results.Redirect("/");
            }
            catch (Exception ex)
            {
                ClearAuthChain(ctx);
                logger.LogError(ex, "Microsoft OAuth callback failed");
                return Results.Redirect("/?azure_error=callback_failed");
            }
        });
    }
}
