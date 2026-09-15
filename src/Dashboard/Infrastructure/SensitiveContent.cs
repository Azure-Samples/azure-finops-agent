using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AzureFinOps.Dashboard.Infrastructure;

internal static class SensitiveContent
{
    internal const string RejectedMessage = "Credentials must not be sent in chat or tool arguments. Use an SSH public key, managed identity, or a reviewed script with local secret inputs. Rotate any credential already disclosed.";
    private const string Redacted = "[REDACTED]";
    private static readonly HashSet<string> SecretFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "adminpassword", "passwd", "pwd", "clientsecret", "accesstoken", "refreshtoken",
        "authorization", "apikey", "accountkey", "primarykey", "secondarykey", "connectionstring",
        "sharedaccesskey", "privatekey", "secretvalue"
    };
    private static readonly Regex[] SecretPatterns =
    [
        new(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
        new(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
        new(@"(?:[?&]|\b)(?:sig|client_secret|AccountKey|SharedAccessKey)=[^&;\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
        new("\\b(?:password|passwd|adminPassword|client[ _-]?secret|api[ _-]?key)\\s*[\"']?\\s*(?:is\\s+|[:=]\\s*)[\"']?[^\\s\"',;}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
        new(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\s\S]*?(?:-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\z)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))
    ];

    internal static bool ContainsSecret(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return !string.Equals(content, Redact(content), StringComparison.Ordinal);
    }

    internal static string Redact(string content)
    {
        try
        {
            var trimmed = content.AsSpan().TrimStart();
            if (!trimmed.IsEmpty && (trimmed[0] == '{' || trimmed[0] == '['))
            {
                var node = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
                if (node is not null) return RedactNode(node) ? node.ToJsonString() : content;
            }
            return RedactText(content);
        }
        catch (JsonException) { return RedactText(content); }
        catch (RegexMatchTimeoutException) { return Redacted; }
    }

    private static bool RedactNode(JsonNode node)
    {
        var changed = false;
        if (node is JsonObject properties)
        {
            foreach (var property in properties.ToArray())
            {
                var normalized = new string(property.Key.Where(char.IsLetterOrDigit).ToArray());
                if (SecretFields.Contains(normalized) && property.Value is not null)
                {
                    if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) && text == Redacted) continue;
                    properties[property.Key] = Redacted;
                    changed = true;
                }
                else if (property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    var clean = Redact(text);
                    if (clean != text) { properties[property.Key] = clean; changed = true; }
                }
                else if (property.Value is not null) changed |= RedactNode(property.Value);
            }
        }
        else if (node is JsonArray elements)
        {
            for (var index = 0; index < elements.Count; index++)
            {
                if (elements[index] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    var clean = Redact(text);
                    if (clean != text) { elements[index] = clean; changed = true; }
                }
                else if (elements[index] is { } element) changed |= RedactNode(element);
            }
        }
        return changed;
    }

    private static string RedactText(string content)
    {
        foreach (var pattern in SecretPatterns) content = pattern.Replace(content, Redacted);
        return content;
    }
}