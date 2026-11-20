using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Credential-free public HTTPS GET used by QueryAzure for documentation, specs, public pricing pages,
/// feeds and public JSON APIs. Connections are only made to public addresses (checked after DNS
/// resolution, on every redirect), no auth or cookies are sent, and downloads are capped.
/// HTML becomes text lines followed by the data records its scripts embed; JSON is returned as-is and XML/CSV are
/// converted to JSON so they can be retained and queried.
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

    private const int MaxBytes = 8_000_000; // pages, JSON/XML/CSV and specs are read whole, then cropped with a LINQ query

    static PublicWebReader()
    {
        // The comment links to the public source. With a bare-domain link (+https://azure-finops-agent.com or
        // +https://example.com), microsoft.com held its Microsoft 365 plan pages open without a byte until the
        // 20-second deadline, while curl's agent and this one load them in about a second.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FinOps-Dashboard/1.0 (+https://github.com/Azure-Samples/azure-finops-agent)");
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

    internal static async Task<string> FetchPageAsync(HttpClient client, Uri uri, CancellationToken cancellationToken, bool timestamp = true)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("PublicWebRequest");
        activity?.SetTag("fetch.host", uri.Host);
        activity?.SetTag("fetch.path", uri.AbsolutePath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (client.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(client.Timeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            var contentType = res.Content.Headers.ContentType?.MediaType ?? "unknown";
            var html = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
            const int cap = MaxBytes;
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

            if (res.IsSuccessStatusCode && !html && total < cap
                && Structured(raw, contentType) is { } structured)
            {
                activity?.SetTag("fetch.structured", true);
                return status + (timestamp ? ResponseShaper.TimestampLine() : "") + structured;
            }

            var body = html ? StripHtml(raw) : raw;
            activity?.SetTag("fetch.output_chars", body.Length);

            var sb = new StringBuilder();
            sb.Append(status);
            sb.AppendLine($"Final URL: {res.RequestMessage?.RequestUri ?? uri}");
            sb.AppendLine($"Content-Type: {contentType}");
            sb.AppendLine($"Bytes on wire: {total}{(total >= cap ? " (size cap reached; the rest of the page was not read)" : "")}");
            sb.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
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

    // Lightweight HTML → text. Drops <script>, <style>, <noscript>, <nav>, <header>, <footer>, <svg> and <form>,
    // puts each table row (cells separated by " | "), paragraph, heading and list item on its own line, strips the
    // remaining tags, decodes entities and collapses whitespace. Not a parser — we don't need a tree, just lines a
    // query can crop by name: a minified page used to become one line of thousands of characters that no crop narrowed.
    private static readonly Regex DropBlocks = new(
        @"<(script|style|noscript|nav|header|footer|svg|form)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    // A row ends at its own </tr>; the atomic, tempered body stops at the next <tr, so an unclosed row never rescans the page.
    private static readonly Regex TableRows = new(
        @"<tr\b[^>]*>((?>(?:(?!</?tr\b).)*))</tr\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CellEnds = new(@"</t[dh]\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockEnds = new(
        @"<br\b[^>]*>|<hr\b[^>]*>|</(?:p|li|h[1-6]|dt|dd|ul|ol|dl|table|thead|tbody|tfoot|caption|section|article|aside|main|blockquote|pre|figcaption|details|summary)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    private static string StripHtml(string html)
    {
        var records = EmbeddedRecords(html);
        var s = DropBlocks.Replace(html, " ");
        s = TableRows.Replace(s, row => "\n" + Tags.Replace(CellEnds.Replace(row.Groups[1].Value, " | "), " ").ReplaceLineEndings(" ") + "\n");
        s = BlockEnds.Replace(s, "\n");
        s = Tags.Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = Whitespace.Replace(s, " ");
        s = string.Join('\n', s.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim().TrimEnd('|').TrimEnd()));
        s = BlankLines.Replace(s, "\n\n").Trim();
        return records.Count == 0 ? s : s + "\n\n" + RecordsHeading + "\n" + string.Join('\n', records);
    }

    // Sites built with frameworks such as Next.js render only part of a table into the HTML (a leaderboard's top
    // entries) and carry the whole data set in script tags, which the text drops: the leaderboards the best-value eval
    // read in November 2026 showed scores for some requested models and none for others, while their scripts held all.
    // Each object in that data that has a name and at least one number becomes one line, "name: X; field: value; …",
    // with a nested object's values as "parent.field", so a query can crop every model's scores by name.
    private const string RecordsHeading = "Records embedded in the page's scripts (the data behind its tables and charts, one per line):";
    private const int MaxRecords = 5_000;
    private const int MaxRecordCharacters = 1_500_000;
    private const int MaxRecordLength = 2_000;
    private const int MaxValueLength = 160;
    private static readonly Regex Scripts = new(@"<script\b([^>]*)>(.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex JsonScriptType = new(@"\btype\s*=\s*[""']?application/(?:ld\+)?json\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Next.js streams its data as self.__next_f.push([1,"<rows>"]); each row is "<id>:<JSON>".
    private static readonly Regex FlightPush = new(@"^\s*self\.__next_f\.push\((\[.*\])\)\s*;?\s*$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FlightRow = new(@"^[0-9a-zA-Z]+:(?=[\[{])", RegexOptions.Compiled);
    private static readonly Regex HexColour = new(@"^#[0-9a-fA-F]{3,8}$", RegexOptions.Compiled);
    private static readonly Regex ImagePath = new(@"\.(?:svg|png|jpe?g|gif|webp|ico|avif)(?:[?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> NameKeys = new(StringComparer.OrdinalIgnoreCase) { "name", "title", "label", "model", "modelName", "displayName", "shortName" };
    private static readonly JsonDocumentOptions EmbeddedJson = new() { MaxDepth = 256 };

    private static List<string> EmbeddedRecords(string html)
    {
        var records = new RecordLines();
        var flight = new StringBuilder();
        foreach (Match script in Scripts.Matches(html))
        {
            var body = script.Groups[2].Value;
            if (JsonScriptType.IsMatch(script.Groups[1].Value)) records.AddJson(body);
            else flight.Append(FlightChunk(body));
        }
        foreach (var row in flight.ToString().Split('\n'))
            if (FlightRow.Match(row) is { Success: true } prefix) records.AddJson(row[prefix.Length..]);
        return records.Lines;
    }

    private static string? FlightChunk(string script)
    {
        if (FlightPush.Match(script) is not { Success: true } push) return null;
        using var document = Parse(push.Groups[1].Value);
        return document?.RootElement is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 1
            && array[1].ValueKind == JsonValueKind.String ? array[1].GetString() : null;
    }

    private static JsonDocument? Parse(string json)
    {
        try { return JsonDocument.Parse(json, EmbeddedJson); }
        catch (JsonException) { return null; }
    }

    private sealed class RecordLines
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private int _characters;
        public List<string> Lines { get; } = [];
        private bool Full => Lines.Count >= MaxRecords || _characters >= MaxRecordCharacters;

        public void AddJson(string json)
        {
            if (Full) return;
            using var document = Parse(json);
            if (document is not null) Walk(document.RootElement);
        }

        private void Walk(JsonElement element)
        {
            if (Full) return;
            if (element.ValueKind == JsonValueKind.Object)
            {
                Record(element);
                foreach (var property in element.EnumerateObject()) Walk(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Walk(item);
        }

        private void Record(JsonElement element)
        {
            string? name = null;
            var numeric = false;
            var fields = new List<string>();
            foreach (var property in element.EnumerateObject())
            {
                if (name is null && property.Value.ValueKind == JsonValueKind.String && NameKeys.Contains(property.Name)
                    && Scalar(property.Value) is { } title)
                {
                    name = $"{property.Name}: {title}";
                    continue;
                }
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var inner in property.Value.EnumerateObject())
                        if (Scalar(inner.Value) is { } nested)
                        {
                            fields.Add($"{property.Name}.{inner.Name}: {nested}");
                            numeric |= inner.Value.ValueKind == JsonValueKind.Number;
                        }
                }
                else if (Scalar(property.Value) is { } value)
                {
                    fields.Add($"{property.Name}: {value}");
                    numeric |= property.Value.ValueKind == JsonValueKind.Number;
                }
            }
            if (name is null || !numeric) return;
            var line = new StringBuilder(name);
            foreach (var field in fields)
            {
                if (line.Length + field.Length + 2 > MaxRecordLength)
                {
                    line.Append("; …");
                    break;
                }
                line.Append("; ").Append(field);
            }
            var text = line.ToString();
            if (_seen.Add(text))
            {
                Lines.Add(text);
                _characters += text.Length + 1;
            }
        }

        // Numbers, booleans and readable strings; framework references ($L1b, $undefined), GUIDs, colours and image paths
        // carry nothing a reader needs.
        private static string? Scalar(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    var raw = value.GetRawText();
                    return raw.Length <= 12 || !value.TryGetDouble(out var number) ? raw : number.ToString("G6", CultureInfo.InvariantCulture);
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                case JsonValueKind.String:
                    var text = value.GetString()?.ReplaceLineEndings(" ").Trim();
                    if (string.IsNullOrEmpty(text)
                        || text.Length > 1 && text[0] == '$' && (char.IsLetter(text[1]) || text[1] is '@' or '$')
                        || Guid.TryParse(text, out _) || HexColour.IsMatch(text) || ImagePath.IsMatch(text)) return null;
                    return text.Length <= MaxValueLength ? text : text[..MaxValueLength] + "…";
                default:
                    return null;
            }
        }
    }
}
