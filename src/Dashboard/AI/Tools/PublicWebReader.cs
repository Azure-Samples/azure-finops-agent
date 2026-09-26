using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Credential-free public HTTPS GET used by QueryAzure for documentation, specs, public pricing pages,
/// feeds and public JSON APIs. Connections are only made to public addresses (checked after DNS
/// resolution, on every redirect), no auth or cookies are sent, and downloads are capped.
/// HTML is stripped to text; JSON is returned as-is and XML/CSV are converted to JSON so they can be retained and queried.
/// </summary>
internal static class PublicWebReader
{
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectPublicAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestVersion = HttpVersion.Version20,
    };

    private const int MaxHtmlBytes = 600_000;         // pages are read as text
    private const int MaxStructuredBytes = 8_000_000; // JSON/XML/CSV/specs are retained and queried instead of inlined
    internal const int MaxOutputChars = 60_000;

    static PublicWebReader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FinOps-Dashboard/1.0 (+https://azure-finops-agent.com)");
        Http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json,application/xml,text/plain,*/*;q=0.8");
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <summary>Rejects literal loopback/private hosts before any DNS lookup; resolution is re-checked at connect time.</summary>
    internal static bool IsBlockedHost(Uri uri) =>
        uri.IsLoopback
        || uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && !IsPublicAddress(address);

    /// <summary>Learn search requires locale; add the documented default instead of letting the call fail.</summary>
    internal static Uri Canonicalize(Uri uri)
    {
        if (!uri.IdnHost.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Equals("/api/search", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(uri.Query, @"[?&]locale=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return uri;
        return new UriBuilder(uri) { Query = (uri.Query.Length > 1 ? uri.Query[1..] + "&" : "") + "locale=en-us" }.Uri;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(bytes[0] is 0 or 10 or 127 or >= 224
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0
                || bytes[0] == 198 && bytes[1] is 18 or 19
                || address.Equals(AzurePlatformAddress));
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Any) || (bytes[0] & 0xFE) == 0xFC);
        return false;
    }

    // Azure's virtual public address for platform services (DHCP, DNS, health) is not an internet endpoint.
    private static readonly IPAddress AzurePlatformAddress = IPAddress.Parse("168.63.129.16");

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)).Where(IsPublicAddress).ToArray();
        if (addresses.Length == 0) throw new HttpRequestException("The host does not resolve to a public address.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static async Task<string> FetchPageAsync(HttpClient client, Uri uri, string? grepFor,
        int maxChars, CancellationToken cancellationToken, bool timestamp = true)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("PublicWebRequest");
        activity?.SetTag("fetch.host", uri.Host);
        activity?.SetTag("fetch.path", uri.AbsolutePath);
        activity?.SetTag("fetch.has_grep", !string.IsNullOrWhiteSpace(grepFor));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (client.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(client.Timeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            var contentType = res.Content.Headers.ContentType?.MediaType ?? "unknown";
            var html = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
            var grep = !string.IsNullOrWhiteSpace(grepFor);
            var cap = html || grep ? MaxHtmlBytes : MaxStructuredBytes;
            activity?.SetTag("fetch.status_code", (int)res.StatusCode);
            activity?.SetTag("fetch.content_type", contentType);

            await using var stream = await res.Content.ReadAsStreamAsync(deadline.Token);
            using var ms = new MemoryStream();
            var buffer = new byte[16_384];
            var total = 0;
            int read;
            while (total < cap && (read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, cap - total)), deadline.Token)) > 0)
            {
                ms.Write(buffer, 0, read);
                total += read;
            }
            var raw = Encoding.UTF8.GetString(ms.ToArray());
            activity?.SetTag("fetch.bytes", total);
            var status = $"HTTP {(int)res.StatusCode} {res.StatusCode}\n";

            if (res.IsSuccessStatusCode && !html && !grep && total < cap
                && Structured(raw, contentType) is { } structured)
            {
                activity?.SetTag("fetch.structured", true);
                return status + (timestamp ? ResponseShaper.TimestampLine() : "") + structured;
            }

            var body = html ? StripHtml(raw) : raw;

            if (grep)
            {
                var needle = grepFor!.Trim();
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
            sb.Append(status);
            sb.AppendLine($"Final URL: {res.RequestMessage?.RequestUri ?? uri}");
            sb.AppendLine($"Content-Type: {contentType}");
            sb.AppendLine($"Bytes on wire: {total}{(total >= cap ? " (HARD CAP — refine URL or use grepFor)" : "")}");
            sb.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
            if (truncated) sb.AppendLine($"[TRUNCATED to {maxChars} chars — pass grepFor or a more specific URL to narrow]");
            sb.AppendLine();
            sb.Append(body);
            return sb.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            activity?.SetTag("fetch.error", ex.GetType().Name);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
            return $"Error: public web request failed ({ex.GetType().Name}). The source is unavailable; do not infer missing content.";
        }
    }

    // JSON (including JSON served as text/plain, such as raw GitHub specs), XML feeds and CSV become queryable JSON.
    private static string? Structured(string raw, string contentType)
    {
        var text = raw.TrimStart('\uFEFF').Trim();
        if (text.Length == 0) return null;
        if (text[0] is '{' or '[')
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(text);
                return text;
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
        if (text[0] == '<' && (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<?xml", StringComparison.Ordinal)))
            return ResponseShaper.XmlToJson(text);
        return contentType.Contains("csv", StringComparison.OrdinalIgnoreCase) ? ResponseShaper.CsvToJson(raw) : null;
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
