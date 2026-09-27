using System.Text.Json;

namespace AzureFinOps.Dashboard.AI.Tools;

// JSON-carrying tool parameters are typed JsonElement?, so the model passes native JSON that arrives already parsed.
// A JSON string is still read (surrounding whitespace and control characters trimmed, trailing commas and comments allowed) but never repaired: invalid JSON is rejected.
internal static class ModelJson
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };
    private static readonly char[] Padding = [.. Enumerable.Range(0, 33).Select(code => (char)code)];

    internal static string Text(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString() ?? "",
        { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } json => json.GetRawText(),
        _ => "",
    };

    internal static JsonDocument? TryParse(string text, JsonValueKind root)
    {
        try
        {
            var document = JsonDocument.Parse(text.Trim(Padding), Options);
            if (document.RootElement.ValueKind == root) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        return null;
    }
}