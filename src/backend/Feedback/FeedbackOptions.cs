using System.Text;
using System.Text.RegularExpressions;

namespace AzureFinOps.Dashboard.Feedback;

/// <summary>
/// Settings for the in-app feedback form (configuration section <c>GitHubFeedback</c>). With a GitHub App
/// (<c>AppId</c> and <c>PrivateKey</c>) installed on <c>Repository</c>, feedback becomes an issue there
/// directly; without one, the form hands the user GitHub's new-issue page with the feedback filled in.
/// </summary>
public sealed partial class FeedbackOptions
{
    public const string DefaultRepository = "Azure-Samples/azure-finops-agent";

    /// <summary>The <c>owner/name</c> repository that receives the issues.</summary>
    public string Repository { get; private init; } = DefaultRepository;

    /// <summary>The GitHub App's client ID or numeric app ID, used as the JWT issuer.</summary>
    public string? AppId { get; private init; }

    /// <summary>The app's PEM private key.</summary>
    public string? PrivateKeyPem { get; private init; }

    /// <summary>Optional; otherwise the installation on <see cref="Repository"/> is looked up.</summary>
    public long? InstallationId { get; private init; }

    public string RepositoryName => Repository[(Repository.IndexOf('/') + 1)..];

    public static FeedbackOptions FromConfiguration(IConfiguration configuration, ILogger logger)
    {
        var section = configuration.GetSection("GitHubFeedback");
        var repository = section["Repository"]?.Trim();
        if (string.IsNullOrEmpty(repository))
            repository = DefaultRepository;
        else if (!RepositoryPattern().IsMatch(repository))
        {
            logger.LogWarning("GitHubFeedback:Repository is not an owner/name pair; feedback goes to GitHub's new-issue page of {Repository}.", DefaultRepository);
            return new FeedbackOptions();
        }

        var appId = section["AppId"]?.Trim();
        var key = NormalizePrivateKey(section["PrivateKey"]);
        if (!string.IsNullOrEmpty(appId) && key is null)
            logger.LogWarning("GitHubFeedback:AppId is set without a readable PrivateKey; feedback uses GitHub's new-issue page.");
        var hasApp = !string.IsNullOrEmpty(appId) && key is not null;
        return new FeedbackOptions
        {
            Repository = repository,
            AppId = hasApp ? appId : null,
            PrivateKeyPem = hasApp ? key : null,
            InstallationId = long.TryParse(section["InstallationId"], out var installation) && installation > 0 ? installation : null,
        };
    }

    /// <summary>
    /// Accepts the PEM as GitHub issues it, with its newlines written as <c>\n</c>, or base64-encoded:
    /// App Service settings are single-line.
    /// </summary>
    internal static string? NormalizePrivateKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
            catch (FormatException) { return null; }
            if (!text.Contains("-----BEGIN", StringComparison.Ordinal)) return null;
        }
        return text.Replace("\\n", "\n", StringComparison.Ordinal);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();
}
