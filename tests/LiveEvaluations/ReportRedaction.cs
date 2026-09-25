using System.Text.RegularExpressions;

namespace LiveEvaluations;

public static partial class ReportRedaction
{
    public static string Apply(string text)
    {
        text = Bearer().Replace(text, "Bearer [redacted]");
        text = Jwt().Replace(text, "[redacted-token]");
        text = Credential().Replace(text, "$1=[redacted]");
        text = ResourceId().Replace(text, "[resource-id]");
        text = Url().Replace(text, "[url]");
        return IpAddress().Replace(text, "[ip-address]");
    }

    [GeneratedRegex(@"\bBearer\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex Jwt();
    [GeneratedRegex(@"\b(api[_ -]?key|client[_ -]?secret|password|accountkey|access[_ -]?token|refresh[_ -]?token|sig)\s*[:=]\s*[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex Credential();
    [GeneratedRegex(@"/subscriptions/[0-9a-f-]+(?:/[^\s|<>""'`]+)*", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceId();
    [GeneratedRegex(@"https?://[^\s<>""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IpAddress();
}