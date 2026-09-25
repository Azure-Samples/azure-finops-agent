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

    internal static object Describe(Entry entry) => new
    {
        kind = "queryable_tool_result",
        resultId = entry.Id,
        source = entry.Source,
        bytes = entry.Bytes,
        sha256 = entry.Sha256,
        expiresUtc = entry.ExpiresUtc,
        storedComplete = true,
        inlineComplete = false,
        schema = Discover(entry.Data),
        queryTool = "QueryToolResult",
        guidance = "The complete redacted source JSON is retained unchanged for this owner and conversation. Copy resultId exactly, including case, and query it with JSONPath after inspecting schema; do not refetch Azure or narrow the requested scope because of response size. Source success, freshness and partial coverage still apply. IDs expire after 30 minutes or a host restart."
    };

    // Inserted as the first root property so every existing JSON consumer still parses the result unchanged.
    internal static string AnnotateInline(string text, Entry entry)
    {
        var start = entry.Source.Preamble.Length;
        if (start >= text.Length || text[start] != '{') return text;
        var handle = JsonSerializer.Serialize(new
        {
            resultId = entry.Id,
            queryTool = "QueryToolResult",
            expiresUtc = entry.ExpiresUtc,
            guidance = "Copy resultId exactly, including case. For totals, sums, counts, top-N, remainders or cross-row comparisons, aggregate this exact result with QueryToolResult instead of mental arithmetic; this annotation is not source data."
        });
        var empty = text.AsSpan(start + 1).TrimStart().StartsWith("}");
        return text[..(start + 1)] + "\"_resultQuery\":" + handle + (empty ? "" : ",") + text[(start + 1)..];
    }

    internal static object Discover(JsonElement data)
    {
        var fields = new Dictionary<string, (HashSet<string> Types, int Observations, int? MinItems, int? MaxItems)>(StringComparer.Ordinal);
        var nodes = 0;
        var complete = true;
        void Visit(JsonElement value, string path, int depth)
        {
            if (++nodes > 100000 || depth > 12 || path.Length > 512 || fields.Count >= 128 && !fields.ContainsKey(path)) { complete = false; return; }
            if (!fields.TryGetValue(path, out var field)) field = (new(StringComparer.Ordinal), 0, null, null);
            field.Types.Add(value.ValueKind.ToString().ToLowerInvariant());
            field.Observations++;
            if (value.ValueKind == JsonValueKind.Array)
            {
                var length = value.GetArrayLength();
                field.MinItems = Math.Min(field.MinItems ?? length, length);
                field.MaxItems = Math.Max(field.MaxItems ?? length, length);
            }
            fields[path] = field;
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                {
                    if (nodes >= 100000) { complete = false; break; }
                    Visit(property.Value, path + "[" + JsonSerializer.Serialize(property.Name) + "]", depth + 1);
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
            fields = fields.Select(field => new
            {
                path = field.Key,
                types = field.Value.Types.Order().ToArray(),
                observations = field.Value.Observations,
                minItems = field.Value.MinItems,
                maxItems = field.Value.MaxItems
            }).ToArray()
        };
    }
}