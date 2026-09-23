using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Public web fetch — no auth, HTTPS only, GET only. Used as the "rung 4/5" of the
/// Persistence escalation ladder when typed APIs (Azure / Graph / Log Analytics /
/// retail prices / pricesheet) cannot answer the question — vendor pricing pages,
/// Microsoft Learn / AWS / GCP docs, GitHub raw specs, vendor changelogs, etc.
/// HTML is stripped to plain text and capped to keep responses bounded.
/// </summary>
public static class WebFetchTools
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestVersion = HttpVersion.Version20,
    };

    private const int MaxBytes = 600_000;       // ~600KB hard cap on the wire
    private const int MaxOutputChars = 60_000;  // ~60KB returned to the LLM after stripping

    static WebFetchTools()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FinOps-Dashboard/1.0 (+https://azure-finops-agent.com)");
        Http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json,application/xml,text/plain,*/*;q=0.8");
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(FetchPublicWebPage, "FetchPublicWebPage",
            @"PUBLIC WEB FETCH (no auth, HTTPS only, GET only). Use this whenever a typed Azure / Graph / Log Analytics tool cannot answer — third-party SaaS / license pricing pages, Microsoft Learn docs, AWS/GCP docs, vendor changelogs, GitHub raw specs, vendor /pricing pages, vendor admin docs, regulatory rate cards, FX, etc. This is rung 4/5 of the Persistence escalation ladder.

USE THIS TOOL EAGERLY — it is the difference between answering 'I don't know' (forbidden) and answering with a real number.

Common patterns:
- Azure pricing detail page: https://azure.microsoft.com/en-us/pricing/details/{service}/  (e.g. .../cognitive-services/openai-service/, .../virtual-machines/, .../storage/blobs/)
- Microsoft Learn: https://learn.microsoft.com/{path} (model cards, API references, concept pages)
- Azure REST specs: https://raw.githubusercontent.com/Azure/azure-rest-api-specs/main/specification/{rp}/...
- Third-party SaaS pricing: https://github.com/pricing, https://www.datadoghq.com/pricing/, https://www.snowflake.com/pricing/, https://openai.com/api/pricing/, https://www.databricks.com/product/pricing, https://www.mongodb.com/pricing, etc.
- AWS / GCP pricing: https://aws.amazon.com/{service}/pricing/, https://cloud.google.com/{service}/pricing
- Vendor changelogs / release notes for new SKU / model availability.

Returns: HTTP status, final URL (after redirects), content-type, and the body. HTML is stripped to plain text (script/style/nav removed); JSON / XML / plain text are returned as-is. Capped at ~60KB after stripping — if truncated, refine with a deeper / more specific URL or a fragment.

Limits: HTTPS only. GET only. No cookies, no auth headers. Per-request cap ~600KB on the wire. 20s timeout.");
    }

    private static async Task<string> FetchPublicWebPage(
        [Description("Full HTTPS URL to fetch. Must start with https://. No query-string secrets.")] string url,
        [Description("Optional substring to grep for in the body — only lines containing this substring are returned. Useful for SKU names, model names, line items on long pricing pages. Empty = return everything (truncated).")] string? grepFor = null,
        [Description("Max characters returned after stripping (default 60000, max 200000). Lower = faster.")] int maxChars = 60_000)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "Error: url must be a valid absolute https:// URL.";

        maxChars = Math.Clamp(maxChars, 1_000, 200_000);

        using var activity = HttpHelper.Telemetry.StartActivity("FetchPublicWebPage");
        activity?.SetTag("fetch.host", uri.Host);
        activity?.SetTag("fetch.path", uri.AbsolutePath);
        activity?.SetTag("fetch.has_grep", !string.IsNullOrWhiteSpace(grepFor));

        HttpResponseMessage res;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            activity?.SetTag("fetch.error", ex.GetType().Name);
            return $"Error: fetch failed ({ex.GetType().Name}: {ex.Message}). URL={uri}. Try a different URL or escalate to another source per the Persistence rule.";
        }

        var contentType = res.Content.Headers.ContentType?.MediaType ?? "unknown";
        activity?.SetTag("fetch.status_code", (int)res.StatusCode);
        activity?.SetTag("fetch.content_type", contentType);

        // Read up to MaxBytes only.
        await using var stream = await res.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buffer = new byte[16_384];
        var total = 0;
        int read;
        while (total < MaxBytes && (read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, MaxBytes - total))) > 0)
        {
            ms.Write(buffer, 0, read);
            total += read;
        }
        var raw = Encoding.UTF8.GetString(ms.ToArray());
        activity?.SetTag("fetch.bytes", total);

        var body = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            ? StripHtml(raw)
            : raw;

        if (!string.IsNullOrWhiteSpace(grepFor))
        {
            var needle = grepFor.Trim();
            var matched = body.Split('\n')
                .Where(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Take(500)
                .ToList();
            body = matched.Count == 0
                ? $"[grep '{needle}' returned 0 matches in {body.Length} chars of body]"
                : string.Join('\n', matched);
            activity?.SetTag("fetch.grep_matches", matched.Count);
        }

        var truncated = false;
        if (body.Length > maxChars)
        {
            body = body[..maxChars];
            truncated = true;
        }

        activity?.SetTag("fetch.output_chars", body.Length);
        activity?.SetTag("fetch.truncated", truncated);

        var sb = new StringBuilder();
        sb.AppendLine($"HTTP {(int)res.StatusCode} {res.StatusCode}");
        sb.AppendLine($"Final URL: {res.RequestMessage?.RequestUri ?? uri}");
        sb.AppendLine($"Content-Type: {contentType}");
        sb.AppendLine($"Bytes on wire: {total}{(total >= MaxBytes ? " (HARD CAP — refine URL)" : "")}");
        sb.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        if (truncated) sb.AppendLine($"[TRUNCATED to {maxChars} chars — pass grepFor or a more specific URL to narrow]");
        sb.AppendLine();
        sb.Append(body);
        return sb.ToString();
    }

    // Lightweight HTML → text. Drops <script>, <style>, <noscript>, <nav>, <header>, <footer>,
    // strips remaining tags, decodes entities, collapses whitespace. Good enough for pricing
    // pages and docs. Not a parser — we don't need a tree, just readable text.
    private static readonly Regex DropBlocks = new(
        @"<(script|style|noscript|nav|header|footer|svg|form)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"(\r?\n){3,}", RegexOptions.Compiled);

    private static string StripHtml(string html)
    {
        var s = DropBlocks.Replace(html, " ");
        s = Tags.Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = Whitespace.Replace(s, " ");
        s = BlankLines.Replace(s, "\n\n");
        return s.Trim();
    }
}
