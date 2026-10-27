using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzureFinOps.Dashboard.Feedback;

/// <summary>A validated submission from the feedback form; every field is the user's own text.</summary>
public sealed record FeedbackRequest(string Kind, string Title, string Details, string? Question, string? Rating);

/// <summary>Why a submission was refused: the field to fix and a message for the user.</summary>
public sealed record FeedbackError(string Field, string Message, bool Sensitive = false);

/// <summary>The issue to create, or to prefill on GitHub's new-issue page.</summary>
public sealed record FeedbackIssue(string Title, string Body, IReadOnlyList<string> Labels);

/// <summary>
/// Turns a feedback form submission into a GitHub issue. The issue is public, so a submission that
/// contains a secret, an ID, an email address or an IP address is refused before anything is sent,
/// and @mentions are neutralised so feedback cannot ping people.
/// </summary>
public static partial class FeedbackReport
{
    public const int MinTitle = 3, MaxTitle = 120, MaxDetails = 4000, MaxQuestion = 1000;
    public const string FeedbackLabel = "feedback";

    // Title prefix and the repository label each kind adds beside "feedback".
    private static readonly Dictionary<string, (string Prefix, string? Label)> Kinds = new(StringComparer.Ordinal)
    {
        ["bug"] = ("Bug", "bug"),
        ["idea"] = ("Idea", "enhancement"),
        ["answer"] = ("Answer quality", null),
        ["other"] = ("Feedback", null),
    };

    private static readonly Dictionary<string, string> Ratings = new(StringComparer.Ordinal)
    {
        ["up"] = "Good answer",
        ["down"] = "Poor answer",
    };

    /// <summary>Reads the form's JSON body; on failure names the field to fix.</summary>
    public static bool TryRead(JsonElement body, out FeedbackRequest request, out FeedbackError? error)
    {
        request = null!;
        error = null;
        if (body.ValueKind != JsonValueKind.Object)
        {
            error = new FeedbackError("form", "Send the feedback form's fields.");
            return false;
        }

        var kind = Text(body, "kind") ?? "";
        if (!Kinds.ContainsKey(kind))
        {
            error = new FeedbackError("kind", "Choose what the feedback is about.");
            return false;
        }

        var title = Clean(Text(body, "title"), singleLine: true);
        if (title.Length < MinTitle)
        {
            error = new FeedbackError("title", "Add a short summary.");
            return false;
        }
        if (title.Length > MaxTitle)
        {
            error = new FeedbackError("title", $"Keep the summary under {MaxTitle} characters.");
            return false;
        }

        var details = Clean(Text(body, "details"), singleLine: false);
        if (details.Length > MaxDetails)
        {
            error = new FeedbackError("details", $"Keep the details under {MaxDetails} characters.");
            return false;
        }

        // The question is the user's own earlier message; a long one is shortened rather than refused.
        var question = Clean(Text(body, "question"), singleLine: false);
        if (question.Length > MaxQuestion) question = question[..MaxQuestion].TrimEnd() + "…";

        foreach (var (field, value) in new[] { ("title", title), ("details", details), ("question", question) })
        {
            if (FindSensitive(value) is { } found)
            {
                error = new FeedbackError(field, $"Remove {found} first: feedback is posted publicly on GitHub.", Sensitive: true);
                return false;
            }
        }

        var rating = Text(body, "rating");
        request = new FeedbackRequest(kind, title, details, question.Length > 0 ? question : null,
            rating is not null && Ratings.ContainsKey(rating) ? rating : null);
        return true;
    }

    /// <summary>Builds the issue's title, Markdown body and labels.</summary>
    public static FeedbackIssue Compose(FeedbackRequest request, string build)
    {
        var (prefix, label) = Kinds[request.Kind];
        var body = new StringBuilder();
        body.Append(request.Details.Length > 0 ? NeutralizeMentions(request.Details) : "_No details given._").Append("\n\n");
        if (request.Question is { } question)
        {
            body.Append("**The question it was about**\n\n");
            foreach (var line in NeutralizeMentions(question).Split('\n'))
                body.Append("> ").Append(line).Append('\n');
            body.Append('\n');
        }

        var facts = new List<string> { $"Type: {prefix}" };
        if (request.Rating is { } rating) facts.Add($"Rating: {Ratings[rating]}");
        facts.Add($"Build: {build}");
        body.Append("---\n<sub>").Append(string.Join(" · ", facts)).Append(" · Sent from the in-app feedback form.</sub>\n");

        var labels = label is null ? new[] { FeedbackLabel } : new[] { FeedbackLabel, label };
        return new FeedbackIssue($"{prefix}: {NeutralizeMentions(request.Title)}", body.ToString(), labels);
    }

    /// <summary>
    /// GitHub's new-issue page with the issue filled in, for posting it with the user's own account.
    /// A long body is shortened so the link stays within what browsers and GitHub accept.
    /// </summary>
    public static string NewIssueUrl(string repository, FeedbackIssue issue, int maxLength = 7000)
    {
        const string shortened = "\n\n_(Shortened to fit in a link: add the rest here.)_";
        var body = issue.Body;
        var url = Url(body);
        while (url.Length > maxLength && body.Length > 0)
        {
            body = body[..(body.Length * 9 / 10)].TrimEnd();
            url = Url(body + shortened);
        }
        return url;

        string Url(string text) =>
            $"https://github.com/{repository}/issues/new?title={Uri.EscapeDataString(issue.Title)}" +
            $"&body={Uri.EscapeDataString(text)}&labels={Uri.EscapeDataString(string.Join(',', issue.Labels))}";
    }

    /// <summary>Names the first kind of private content found, or null.</summary>
    internal static string? FindSensitive(string text)
    {
        if (text.Length == 0) return null;
        if (SecretPattern().IsMatch(text)) return "the secret or key";
        if (GuidPattern().IsMatch(text)) return "the ID (such as a tenant or subscription ID)";
        if (EmailPattern().IsMatch(text)) return "the email address";
        if (Ipv4Pattern().IsMatch(text)) return "the IP address";
        return null;
    }

    /// <summary>A zero-width space after @ keeps the name readable but stops GitHub notifying anyone.</summary>
    internal static string NeutralizeMentions(string text) => MentionPattern().Replace(text, "@\u200B");

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Clean(string? value, bool singleLine)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var kept = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n' or '\t') kept.Append(singleLine ? ' ' : c);
            else if (!char.IsControl(c)) kept.Append(c);
        }
        var cleaned = kept.ToString();
        return singleLine ? WhitespacePattern().Replace(cleaned, " ").Trim() : cleaned.Trim();
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----|\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.|\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})|\b(?:AccountKey|SharedAccessKey|SharedAccessSignature|ClientSecret|Password|Pwd)\s*=\s*\S|[?&]sig=[A-Za-z0-9%+/=]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_`])@(?=[A-Za-z0-9])")]
    private static partial Regex MentionPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
