using Azure.Core;
using Azure.Identity;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Produces the credential form fields for the Entra token endpoint.
///
/// Preferred: <b>federated client assertion</b> — the runtime managed identity
/// (App Service / Container Apps) obtains a JWT for <c>api://AzureADTokenExchange</c>
/// and we present it as <c>client_assertion</c>. No secret is stored anywhere.
/// The Entra app registration must have a federated identity credential pointing
/// to the runtime MI's issuer + subject.
///
/// Fallback: <b>client_secret</b> — only when <see cref="MicrosoftOAuthOptions.ClientSecret"/>
/// is explicitly configured (intended for local development without a federated MI).
/// </summary>
public sealed class EntraClientCredentials
{
    private static readonly TokenRequestContext FederationScope =
        new(new[] { "api://AzureADTokenExchange/.default" });

    private readonly MicrosoftOAuthOptions _options;
    private readonly TokenCredential? _federationCredential;
    private readonly ILogger<EntraClientCredentials> _logger;
    private readonly SemaphoreSlim _assertionLock = new(1, 1);
    private string? _cachedAssertion;
    private DateTimeOffset _cachedAssertionExpiry = DateTimeOffset.MinValue;

    public bool UsesFederation => string.IsNullOrEmpty(_options.ClientSecret);

    public EntraClientCredentials(MicrosoftOAuthOptions options, ILogger<EntraClientCredentials> logger)
    {
        _options = options;
        _logger = logger;
        if (UsesFederation)
        {
            _federationCredential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeInteractiveBrowserCredential = true,
            });
            _logger.LogInformation("Entra credentials: federated client assertion (no secret)");
        }
        else
        {
            _logger.LogWarning("Entra credentials: using client_secret fallback. Prefer federated identity in production.");
        }
    }

    /// <summary>
    /// Append <c>client_secret</c> OR <c>client_assertion[+_type]</c> to the supplied form fields.
    /// </summary>
    public async Task AddCredentialFieldsAsync(IDictionary<string, string> form, CancellationToken ct = default)
    {
        if (!UsesFederation)
        {
            form["client_secret"] = _options.ClientSecret;
            return;
        }

        var assertion = await GetFederatedAssertionAsync(ct);
        form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
        form["client_assertion"] = assertion;
    }

    private async Task<string> GetFederatedAssertionAsync(CancellationToken ct)
    {
        if (_cachedAssertion is not null && DateTimeOffset.UtcNow < _cachedAssertionExpiry)
            return _cachedAssertion;

        await _assertionLock.WaitAsync(ct);
        try
        {
            if (_cachedAssertion is not null && DateTimeOffset.UtcNow < _cachedAssertionExpiry)
                return _cachedAssertion;

            var token = await _federationCredential!.GetTokenAsync(FederationScope, ct);
            _cachedAssertion = token.Token;
            // Refresh 5 min before MI token expiry.
            _cachedAssertionExpiry = token.ExpiresOn.AddMinutes(-5);
            return _cachedAssertion;
        }
        finally
        {
            _assertionLock.Release();
        }
    }
}
