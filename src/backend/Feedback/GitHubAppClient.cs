using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzureFinOps.Dashboard.Feedback;

/// <summary>An issue GitHub created.</summary>
public sealed record CreatedIssue(int Number, string Url);

/// <summary>The GitHub App is not configured, not installed on the repository, or refused; use the link instead.</summary>
public sealed class GitHubFeedbackUnavailableException(string message) : Exception(message);

/// <summary>
/// Creates feedback issues as a GitHub App. The host signs a short-lived app JWT with the private key,
/// exchanges it for an installation token restricted to the one configured repository with only the
/// Issues write permission, and keeps that token in memory until shortly before it expires. Neither the
/// key nor a token is ever logged or returned.
/// </summary>
public sealed class GitHubAppClient : IDisposable
{
    public const string HttpClientName = "github";
    private static readonly Uri ApiBase = new("https://api.github.com/");
    private static readonly TimeSpan AvailableFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UnavailableFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(5);

    private readonly FeedbackOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RSA? _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _installationId;
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private bool _available;
    private DateTimeOffset _availabilityKnownUntil;

    public GitHubAppClient(FeedbackOptions options, IHttpClientFactory http, TimeProvider time, ILogger logger)
    {
        _options = options;
        _http = http;
        _time = time;
        _logger = logger;
        _installationId = options.InstallationId;
        if (options.PrivateKeyPem is null) return;
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(options.PrivateKeyPem);
            _key = key;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            logger.LogWarning("GitHubFeedback:PrivateKey is not an RSA private key in PEM form; feedback uses GitHub's new-issue page.");
        }
    }

    public string Repository => _options.Repository;

    /// <summary>
    /// Whether issues can be created right now: the app is configured and GitHub issues it a token for the
    /// repository. Remembered for ten minutes (two after a failure), so the form's check stays cheap.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (_key is null) return false;
        if (_time.GetUtcNow() < _availabilityKnownUntil) return _available;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_time.GetUtcNow() >= _availabilityKnownUntil)
            {
                try
                {
                    await GetTokenLockedAsync(cancellationToken);
                    Remember(true);
                }
                catch (Exception ex) when (IsGitHubFailure(ex, cancellationToken))
                {
                    _logger.LogWarning("GitHub feedback is unavailable ({Reason}); the form links to GitHub's new-issue page.", Describe(ex));
                    Remember(false);
                }
            }
            return _available;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates the issue. Throws <see cref="GitHubFeedbackUnavailableException"/> when the app cannot.</summary>
    public async Task<CreatedIssue> CreateIssueAsync(FeedbackIssue issue, CancellationToken cancellationToken)
    {
        if (_key is null) throw new GitHubFeedbackUnavailableException("The GitHub App is not configured.");
        var token = await GetTokenAsync(cancellationToken);
        var labels = issue.Labels;
        using var client = _http.CreateClient(HttpClientName);
        for (var attempt = 1; ; attempt++)
        {
            using var request = Request(HttpMethod.Post, $"repos/{_options.Repository}/issues", token,
                JsonContent.Create(new { title = issue.Title, body = issue.Body, labels }));
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Created)
            {
                using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return new CreatedIssue(created.RootElement.GetProperty("number").GetInt32(),
                    created.RootElement.GetProperty("html_url").GetString() ?? "");
            }
            if (attempt < 3 && response.StatusCode == HttpStatusCode.UnprocessableEntity && labels.Count > 0)
            {
                // A label the repository refuses must not lose the feedback.
                labels = [];
                continue;
            }
            if (attempt < 3 && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The token was revoked early: mint another once.
                _token = null;
                token = await GetTokenAsync(cancellationToken);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                Remember(false);
                throw new GitHubFeedbackUnavailableException($"GitHub refused the issue ({(int)response.StatusCode}).");
            }
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }

    /// <summary>The app JWT: RS256, back-dated a minute for clock drift and valid nine, under GitHub's ten-minute limit.</summary>
    internal string CreateAppJwt()
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        object issuer = long.TryParse(_options.AppId, out var appId) ? appId : _options.AppId!;
        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 540, iss = issuer }));
        var signature = _key!.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url.EncodeToString(signature)}";
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await GetTokenLockedAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<string> GetTokenLockedAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && _time.GetUtcNow() < _tokenExpires - TokenMargin) return _token;
        using var client = _http.CreateClient(HttpClientName);
        _installationId ??= await FindInstallationAsync(client, cancellationToken);
        using var request = Request(HttpMethod.Post, $"app/installations/{_installationId}/access_tokens", CreateAppJwt(),
            JsonContent.Create(new { repositories = new[] { _options.RepositoryName }, permissions = new { issues = "write" } }));
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            // The installation was removed or no longer covers the repository: look it up again next time.
            if (_options.InstallationId is null) _installationId = null;
            throw new GitHubFeedbackUnavailableException($"GitHub refused an installation token ({(int)response.StatusCode}).");
        }
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        _token = json.RootElement.GetProperty("token").GetString();
        _tokenExpires = json.RootElement.GetProperty("expires_at").GetDateTimeOffset();
        return _token!;
    }

    private async Task<long> FindInstallationAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, $"repos/{_options.Repository}/installation", CreateAppJwt());
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new GitHubFeedbackUnavailableException(response.StatusCode == HttpStatusCode.NotFound
                ? "The GitHub App is not installed on the repository."
                : $"GitHub refused the app's credentials ({(int)response.StatusCode}).");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id").GetInt64();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string bearer, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, new Uri(ApiBase, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("FinOps-Dashboard/1.0 (+https://github.com/Azure-Samples/azure-finops-agent)");
        return request;
    }

    private void Remember(bool available)
    {
        _available = available;
        _availabilityKnownUntil = _time.GetUtcNow() + (available ? AvailableFor : UnavailableFor);
    }

    internal static bool IsGitHubFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is GitHubFeedbackUnavailableException or HttpRequestException or JsonException or KeyNotFoundException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string Describe(Exception ex) => ex switch
    {
        GitHubFeedbackUnavailableException => ex.Message,
        HttpRequestException { StatusCode: { } status } => $"GitHub answered {(int)status}",
        OperationCanceledException => "GitHub did not answer in time",
        _ => "GitHub could not be reached",
    };

    public void Dispose()
    {
        _key?.Dispose();
        _gate.Dispose();
    }
}
