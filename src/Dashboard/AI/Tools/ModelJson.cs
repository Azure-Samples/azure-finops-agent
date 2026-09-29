using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

// JSON-carrying tool parameters are typed JsonElement?, so the model passes native JSON that arrives already parsed.
// A JSON string is still read (trailing commas and comments allowed) but never repaired: invalid JSON is rejected.
internal static class ModelJson
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };

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
            var document = JsonDocument.Parse(text, Options);
            if (document.RootElement.ValueKind == root) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    // Agent Framework's function invoker calls this before binding, so common model argument quirks do not fail the call.
    internal static void Normalize(AIFunction function, AIFunctionArguments arguments)
    {
        UnwrapCallEnvelope(function.Name, arguments);
        CoerceScalarStrings(function.JsonSchema, arguments);
    }

    // Models occasionally serialize a parallel call as {"recipient_name":"functions.<tool>","parameters":{...}}.
    // Unwrap only that exact envelope, and only when it names the tool actually invoked.
    internal static void UnwrapCallEnvelope(string toolName, AIFunctionArguments arguments)
    {
        if (arguments.Count != 2
            || !arguments.TryGetValue("recipient_name", out var recipient)
            || !arguments.TryGetValue("parameters", out var parameters)) return;
        var recipientName = recipient switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
        if (recipientName != toolName && recipientName != "functions." + toolName) return;
        JsonElement inner;
        switch (parameters)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                inner = element;
                break;
            case string text when text.TrimStart().StartsWith('{'):
                try { using var document = JsonDocument.Parse(text); inner = document.RootElement.Clone(); }
                catch (JsonException) { return; }
                break;
            case JsonElement { ValueKind: JsonValueKind.String } element when element.GetString()!.TrimStart().StartsWith('{'):
                try { using var document = JsonDocument.Parse(element.GetString()!); inner = document.RootElement.Clone(); }
                catch (JsonException) { return; }
                break;
            default:
                return;
        }
        if (inner.ValueKind != JsonValueKind.Object) return;
        arguments.Clear();
        foreach (var property in inner.EnumerateObject()) arguments[property.Name] = property.Value.Clone();
    }

    // Models often send a JSON number, boolean, object or array for a string parameter (for example
    // "limit": 50 or an unquoted queryJson object); argument binding would otherwise fail the whole tool call.
    internal static void CoerceScalarStrings(JsonElement schema, AIFunctionArguments arguments)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object) return;
        foreach (var key in arguments.Keys.ToList())
        {
            if (!properties.TryGetProperty(key, out var property) || !IsStringOnly(property)) continue;
            arguments[key] = arguments[key] switch
            {
                JsonElement { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
                JsonElement { ValueKind: JsonValueKind.True } => "true",
                JsonElement { ValueKind: JsonValueKind.False } => "false",
                JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json => json.GetRawText(),
                bool flag => flag ? "true" : "false",
                int or long or decimal or double or float => Convert.ToString(arguments[key], System.Globalization.CultureInfo.InvariantCulture),
                var value => value
            };
        }

        static bool IsStringOnly(JsonElement property)
        {
            if (!property.TryGetProperty("type", out var type)) return false;
            if (type.ValueKind == JsonValueKind.String) return type.GetString() == "string";
            return type.ValueKind == JsonValueKind.Array
                && type.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() is "string" or "null")
                && type.EnumerateArray().Any(item => item.GetString() == "string");
        }
    }
}
