using System.Collections.Concurrent;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Validates Entra ID id_tokens against per-tenant OIDC metadata (JWKS, issuer,
/// audience, lifetime) and verifies the request <c>nonce</c>. Multi-tenant safe:
/// metadata is fetched and cached per tenant id.
/// </summary>
public sealed class IdTokenValidator
{
    private readonly MicrosoftOAuthOptions _options;
    private readonly ILogger<IdTokenValidator> _logger;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configManagers = new();
    private readonly JsonWebTokenHandler _handler = new();

    public IdTokenValidator(MicrosoftOAuthOptions options, ILogger<IdTokenValidator> logger)
    {
        _options = options;
        _logger = logger;
    }

    public sealed record ValidatedClaims(
        string TenantId,
        string ObjectId,
        string? Name,
        string? Email,
        string? PreferredUsername);

    public async Task<ValidatedClaims?> ValidateAsync(string idToken, string expectedNonce, CancellationToken ct = default)
    {
        // Peek tenant id from unverified token to choose the correct OIDC metadata document.
        // Signature is still validated below — peeking only selects which JWKS to verify against.
        string tenantFromToken;
        try
        {
            var unverified = _handler.ReadJsonWebToken(idToken);
            tenantFromToken = unverified.GetClaim("tid")?.Value ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "id_token is not a parseable JWT");
            return null;
        }

        if (!Guid.TryParse(tenantFromToken, out _))
        {
            _logger.LogWarning("id_token has missing/invalid tid claim");
            return null;
        }

        var configManager = _configManagers.GetOrAdd(tenantFromToken, tid =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"https://login.microsoftonline.com/{tid}/v2.0/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true }));

        OpenIdConnectConfiguration config;
        try
        {
            config = await configManager.GetConfigurationAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch OIDC metadata for tenant {Tid}", tenantFromToken);
            return null;
        }

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = new[]
            {
                $"https://login.microsoftonline.com/{tenantFromToken}/v2.0",
                $"https://sts.windows.net/{tenantFromToken}/",
            },
            ValidateAudience = true,
            ValidAudience = _options.ClientId,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            IssuerSigningKeys = config.SigningKeys,
        };

        var result = await _handler.ValidateTokenAsync(idToken, validationParameters);
        if (!result.IsValid)
        {
            _logger.LogWarning(result.Exception, "id_token validation failed");
            return null;
        }

        var claims = result.Claims;
        var nonce = claims.TryGetValue("nonce", out var nObj) ? nObj?.ToString() : null;
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
        {
            _logger.LogWarning("id_token nonce mismatch — possible replay");
            return null;
        }

        var oid = claims.TryGetValue("oid", out var oObj) ? oObj?.ToString() : null;
        if (!Guid.TryParse(oid, out _))
        {
            _logger.LogWarning("id_token has missing/invalid oid claim");
            return null;
        }

        return new ValidatedClaims(
            TenantId: tenantFromToken,
            ObjectId: oid,
            Name: claims.TryGetValue("name", out var n) ? n?.ToString() : null,
            // email is the preferred display claim; fall back to preferred_username.
            // For nOAuth defense, never use either as an identity key — only oid+tid.
            Email: claims.TryGetValue("email", out var e) ? e?.ToString() : null,
            PreferredUsername: claims.TryGetValue("preferred_username", out var p) ? p?.ToString() : null);
    }
}
