using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzureFinOps.Dashboard.Infrastructure;

internal sealed class ToolResultStore(TimeProvider? clock = null, long capacityBytes = 128 * 1024 * 1024)
{
    internal const int InlineBytes = 32 * 1024;
    internal const int MaxResultBytes = 16 * 1024 * 1024;
    internal sealed record Source(string Tool, DateTimeOffset RetrievedAtUtc, bool Success, bool Fresh, bool Partial, string Preamble);
    internal sealed record Entry(string Id, long Owner, string SessionId, string Text, JsonElement Data,
        int Bytes, string Sha256, Source Source, DateTimeOffset ExpiresUtc);
    internal static ToolResultStore Default { get; } = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal Entry? Retain(long owner, string sessionId, string text, Source source)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxResultBytes || string.IsNullOrWhiteSpace(sessionId)) return null;
        var body = text;
        var preamble = "";
        while (body.StartsWith("HTTP ", StringComparison.Ordinal) || body.StartsWith("Current UTC time: ", StringComparison.Ordinal))
        {
            var end = body.IndexOf('\n');
            if (end < 0) return null;
            preamble += body[..(end + 1)];
            body = body[(end + 1)..];
        }
        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return null;
            data = document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
        var entry = new Entry(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)), owner, sessionId, text, data, bytes,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), source with { Preamble = preamble }, _clock.GetUtcNow().AddMinutes(30));
        lock (_sync)
        {
            Cleanup();
            if (_entries.Count >= 4096 || _entries.Values.Sum(item => (long)item.Bytes) + bytes > capacityBytes
                || _entries.Values.Where(item => item.Owner == owner).Sum(item => (long)item.Bytes) + bytes > 32 * 1024 * 1024) return null;
            _entries.Add(entry.Id, entry);
        }
        return entry;
    }

    internal Entry? Find(long owner, string? sessionId, string id)
    {
        lock (_sync)
        {
            Cleanup();
            return _entries.TryGetValue(id, out var entry) && entry.Owner == owner && entry.SessionId == sessionId ? entry : null;
        }
    }

    private void Cleanup()
    {
        foreach (var entry in _entries.Values.Where(entry => entry.ExpiresUtc <= _clock.GetUtcNow()).ToArray()) _entries.Remove(entry.Id);
    }

    internal static Dictionary<string, object?> Describe(Entry entry, string? resultQueryProblem = null, IReadOnlyList<object>? failedResults = null)
    {
        var description = new Dictionary<string, object?>
        {
            ["kind"] = "queryable_tool_result",
            ["resultId"] = entry.Id,
            ["source"] = entry.Source,
            ["bytes"] = entry.Bytes,
            ["sha256"] = entry.Sha256,
            ["expiresUtc"] = entry.ExpiresUtc,
            ["storedComplete"] = true,
            ["inlineComplete"] = false,
            ["schema"] = Discover(entry.Data),
            ["queryTool"] = "QueryToolResult",
            ["guidance"] = "The complete redacted source JSON is retained unchanged for this owner and conversation. Copy resultId exactly, including case, and query it with JSONPath after inspecting schema; do not refetch Azure or narrow the requested scope because of response size. Source success, freshness and partial coverage still apply. IDs expire after 30 minutes or a host restart."
        };
        if (resultQueryProblem is not null)
            description["resultQueryProblem"] = resultQueryProblem + " The request was not repeated; correct the query against this schema and call QueryToolResult with the resultId.";
        // Failed batch items stay visible (and keep the batch classified as failed) while successful items remain queryable.
        if (failedResults is not null) description["results"] = failedResults;
        return description;
    }

    // Inserted as the first root property so every existing JSON consumer still parses the result unchanged.
    internal static string AnnotateInline(string text, Entry entry, string? resultQueryProblem = null)
    {
        var start = entry.Source.Preamble.Length;
        if (start >= text.Length || text[start] != '{') return text;
        var handle = new Dictionary<string, object?>
        {
            ["resultId"] = entry.Id,
            ["queryTool"] = "QueryToolResult",
            ["expiresUtc"] = entry.ExpiresUtc,
            ["guidance"] = "Copy resultId exactly, including case. For totals, sums, counts, top-N, remainders or cross-row comparisons, aggregate this exact result with QueryToolResult instead of mental arithmetic; this annotation is not source data."
        };
        if (resultQueryProblem is not null) handle["problem"] = resultQueryProblem + " The complete result is included below; correct the query if you still need it.";
        var empty = text.AsSpan(start + 1).TrimStart().StartsWith("}");
        return text[..(start + 1)] + "\"_resultQuery\":" + JsonSerializer.Serialize(handle) + (empty ? "" : ",") + text[(start + 1)..];
    }

    internal static object Discover(JsonElement data)
    {
        var fields = new Dictionary<string, (HashSet<string> Types, int Observations, int? MinItems, int? MaxItems, string? Example)>(StringComparer.Ordinal);
        var tables = new List<object>();
        var nodes = 0;
        var complete = true;
        void Visit(JsonElement value, string path, int depth)
        {
            if (++nodes > 100000 || depth > 12 || path.Length > 512 || fields.Count >= 128 && !fields.ContainsKey(path)) { complete = false; return; }
            if (!fields.TryGetValue(path, out var field)) field = (new(StringComparer.Ordinal), 0, null, null, null);
            field.Types.Add(value.ValueKind.ToString().ToLowerInvariant());
            field.Observations++;
            if (value.ValueKind == JsonValueKind.Array)
            {
                var length = value.GetArrayLength();
                field.MinItems = Math.Min(field.MinItems ?? length, length);
                field.MaxItems = Math.Max(field.MaxItems ?? length, length);
            }
            else if (field.Example is null && value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                var example = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
                field.Example = example.Length > 60 ? example[..57] + "..." : example;
            }
            fields[path] = field;
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (tables.Count < 8 && Table(value, path) is { } table) tables.Add(table);
                foreach (var property in value.EnumerateObject())
                {
                    if (nodes >= 100000) { complete = false; break; }
                    Visit(property.Value, path + "[" + JsonSerializer.Serialize(property.Name) + "]", depth + 1);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                {
                    if (nodes >= 100000) { complete = false; break; }
                    Visit(item, path + "[*]", depth + 1);
                }
        }
        Visit(data, "$", 0);
        return new
        {
            complete,
            nodesExamined = nodes,
            tables = tables.Count == 0 ? null : tables,
            fields = fields.Select(field => new
            {
                path = field.Key,
                types = field.Value.Types.Order().ToArray(),
                observations = field.Value.Observations,
                minItems = field.Value.MinItems,
                maxItems = field.Value.MaxItems,
                example = field.Value.Example
            }).ToArray()
        };
    }

    // Columnar tables (Cost Management, Log Analytics, converted CSV): rows are addressable by column name.
    private static object? Table(JsonElement value, string path)
    {
        if (!value.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array || columns.GetArrayLength() == 0
            || !value.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;
        var names = columns.EnumerateArray().Select(column => column.ValueKind switch
        {
            JsonValueKind.Object when column.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String => name.GetString(),
            JsonValueKind.String => column.GetString(),
            _ => null,
        }).ToArray();
        if (names.Any(string.IsNullOrEmpty)) return null;
        var rowsPath = path + "[\"rows\"][*]";
        var example = System.Text.RegularExpressions.Regex.IsMatch(names[0]!, "^[A-Za-z_][A-Za-z0-9_]*$") ? "$." + names[0] : "$['" + names[0]!.Replace("'", "\\'") + "']";
        return new
        {
            rowsPath,
            rowCount = rows.GetArrayLength(),
            columns = names.Take(60).ToArray(),
            usage = $"path {rowsPath} returns one record per row keyed by column name, e.g. {example}"
        };
    }
}