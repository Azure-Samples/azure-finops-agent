using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzureFinOps.Dashboard.AI.Tools;

// Models writing long JSON inside a string argument sometimes garble only its structural brackets.
// Repairs never add, drop or change data: they close containers the text left open, drop trailing
// brackets, commas, one bare leaked token or leaked tool-channel text, drop a closer that matches no open container, and close
// the open objects before a sibling item that the text starts inside an array. A repair must still parse
// as the expected root kind; callers validate the result exactly as they validate any other input.
internal static partial class ModelJson
{
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };

    internal static JsonDocument? TryParse(string text, JsonValueKind root) =>
        Parse(text, root) ?? (Repair(text) is { } repaired ? Parse(repaired, root) : null);

    private static JsonDocument? Parse(string text, JsonValueKind root)
    {
        try
        {
            var document = JsonDocument.Parse(text, Lenient);
            if (document.RootElement.ValueKind == root) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    internal static string? Repair(string text)
    {
        var output = new StringBuilder(text.Length + 8);
        var stack = new Stack<char>();
        bool inString = false, escaped = false, expectKey = false, started = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                output.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (started && stack.Count == 0)
                return TrailingNoise().IsMatch(text[index..]) || LeakedToolChannel().IsMatch(text[index..]) ? output.ToString() : null;
            switch (character)
            {
                case '"':
                    inString = true;
                    expectKey = false;
                    output.Append(character);
                    break;
                case '{' or '[':
                    if (stack.Count > 0 && stack.Peek() == '{' && expectKey)
                    {
                        var comma = LastSignificant(output);
                        if (comma < 0 || output[comma] != ',') return null;
                        output.Length = comma;
                        while (stack.Count > 0 && stack.Peek() == '{') { stack.Pop(); output.Append('}'); }
                        if (stack.Count == 0) return null;
                        output.Append(',');
                    }
                    stack.Push(character);
                    started = true;
                    expectKey = character == '{';
                    output.Append(character);
                    break;
                case '}' or ']':
                    var open = character == '}' ? '{' : '[';
                    if (!stack.Contains(open)) break;
                    while (stack.Peek() != open) output.Append(stack.Pop() == '{' ? '}' : ']');
                    stack.Pop();
                    expectKey = false;
                    output.Append(character);
                    break;
                case ',':
                    expectKey = stack.Count > 0 && stack.Peek() == '{';
                    output.Append(character);
                    break;
                default:
                    if (!char.IsWhiteSpace(character)) expectKey = false;
                    output.Append(character);
                    break;
            }
        }
        if (inString || !started) return null;
        while (stack.Count > 0) output.Append(stack.Pop() == '{' ? '}' : ']');
        return output.ToString();
    }

    private static int LastSignificant(StringBuilder text)
    {
        for (var index = text.Length - 1; index >= 0; index--)
            if (!char.IsWhiteSpace(text[index])) return index;
        return -1;
    }

    // Leaked brackets, commas and at most one bare token (such as an argument name) carry no data.
    [GeneratedRegex(@"^[\s\[\]{},]*[A-Za-z0-9_?]*[\s\[\]{},]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TrailingNoise();

    // The model's own tool-call channel text ("... assistant to=functions.<tool> ...") leaked after a complete root:
    // text that starts as prose rather than a JSON value and carries that marker is never part of the single root.
    [GeneratedRegex(@"^[\s\[\]{},]*[A-Za-z][\s\S]*?to=functions\.", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex LeakedToolChannel();
}
