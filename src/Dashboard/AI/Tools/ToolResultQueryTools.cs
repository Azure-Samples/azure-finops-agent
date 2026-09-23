using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;

namespace AzureFinOps.Dashboard.AI.Tools;

public sealed class ToolResultQueryTools(long owner)
{
    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(QueryToolResult, "QueryToolResult", "Query a retained large tool result without calling Azure again. Use only the opaque resultId returned by a tool in this conversation. Inspect its dynamic schema, then pass a JSON query using JSONPath for nested arrays and filters. The original redacted JSON is retained unchanged; this tool never executes code, accesses model-chosen files, or performs network requests. Prefer source-side filtering in the original API call first. Aggregate or filter the full retained selection before paging; keep source success, freshness, partial coverage, totalMatches, totalResults, complete and nextOffset. A complete page does not make an incomplete Azure source complete. For scheduled evidence cite the original source tool, not this local view. Expired or inaccessible results fail closed.");
    }

    private async Task<string> QueryToolResult(
        [Description("Opaque resultId from a queryable_tool_result response in this conversation; never a file path or URL.")] string resultId,
        [Description("JSON object: mode=query (default) or schema; path is JSONPath, default $. Select array rows with $.rows[*]. Optional where is an array of up to 12 AND conditions {path,op,value} evaluated per row, op=eq|ne|in|notIn|gt|gte|lt|lte|contains|containsAny|startsWith|endsWith|exists (case-insensitive text, no regex), e.g. [{\"path\":\"$[1]\",\"op\":\"containsAny\",\"value\":[\"virtualMachines\"]}]. Optional select maps output names to per-row JSONPaths, e.g. {\"region\":\"$.region\",\"quota\":\"$.result.QuotaStatus\"}. Optional groupBy maps up to 6 names to per-row paths and aggregates is [{op:count|sum|avg|min|max,path:$.cost,as:total}]; count needs no path. Optional sort:[{path:$.total,direction:desc|asc}], offset=0, limit=50 (max 200, 0 for totals). select plus aggregates (without groupBy) returns the selected rows and overall totals across every match. Schema mode supports a path; several matches are described as one array. No code, paths, URLs, owner or source overrides.")] string queryJson = "{}",
        CancellationToken cancellationToken = default)
    {
        var context = ToolExecutionContext.Current;
        if (context?.UserId != owner) return Unavailable;
        var entry = ToolResultStore.Default.Find(owner, context.SessionId, resultId);
        if (entry is null) return Unavailable;
        await QuerySlots.WaitAsync(cancellationToken);
        try { return Execute(entry, queryJson, cancellationToken); }
        finally { QuerySlots.Release(); }
    }

    private const string Unavailable = "Error: This result is unavailable or expired for this conversation.";
    private static readonly HashSet<string> QueryKeys = ["mode", "path", "where", "select", "groupBy", "aggregates", "sort", "offset", "limit"];
    private static readonly SemaphoreSlim QuerySlots = new(2, 2);

    internal static string Execute(ToolResultStore.Entry entry, string queryJson, CancellationToken cancellationToken = default)
    {
        try
        {
            if (queryJson.Length > 16000) return "Error: Query exceeds the 16000-character budget.";
            using var document = JsonDocument.Parse(queryJson);
            var query = document.RootElement;
            Require(query.ValueKind == JsonValueKind.Object, "Query must be a JSON object.");
            var keys = new HashSet<string>();
            foreach (var property in query.EnumerateObject()) Require(QueryKeys.Contains(property.Name) && keys.Add(property.Name), "Unknown, reserved or duplicate query parameter.");
            var mode = String(query, "mode", "query");
            Require(mode is "query" or "schema", "Mode must be query or schema.");
            var watch = Stopwatch.StartNew();
            var steps = 0;
            long projectedCharacters = 0;
            void ReserveProjection(int characters)
            {
                projectedCharacters += characters;
                Require(projectedCharacters <= 8 * 1024 * 1024, "Projection exceeds its work budget; filter the selection or request fewer fields.");
            }
            void CheckBudget()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++steps > 2000000) throw new QueryException("Query work budget exceeded; use a narrower selector.");
                if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new QueryException("Query time budget exceeded; use a narrower selector.");
            }
            CheckBudget();
            using var textReader = new StringReader(entry.Data.GetRawText());
            using var jsonReader = new Newtonsoft.Json.JsonTextReader(textReader)
            {
                DateParseHandling = Newtonsoft.Json.DateParseHandling.None,
                FloatParseHandling = Newtonsoft.Json.FloatParseHandling.Decimal,
                MaxDepth = 64
            };
            var root = JToken.Load(jsonReader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            var path = String(query, "path", "$");
            var selected = Select(root, path, CheckBudget).ToArray();
            if (query.TryGetProperty("where", out var where))
            {
                var conditions = Conditions(where);
                selected = selected.Where(row => conditions.All(condition => condition.Matches(Single(row, condition.Path, CheckBudget)))).ToArray();
            }
            if (mode == "schema")
            {
                using var selectedDocument = JsonDocument.Parse(selected.Length == 1
                    ? selected[0].ToString(Newtonsoft.Json.Formatting.None)
                    : new JArray(selected).ToString(Newtonsoft.Json.Formatting.None));
                return JsonSerializer.Serialize(new
                {
                    resultId = entry.Id,
                    source = entry.Source,
                    sha256 = entry.Sha256,
                    expiresUtc = entry.ExpiresUtc,
                    selectedValues = selected.Length,
                    schemaRoot = selected.Length == 1 ? "selected value" : "array of all selected values ($[*] is each match)",
                    schema = ToolResultStore.Discover(selectedDocument.RootElement)
                });
            }
            var projection = Paths(query, "select", 30);
            var groups = Paths(query, "groupBy", 6);
            string? note = null;
            if (projection.Count > 0 && groups.Count > 0)
            {
                projection.Clear();
                note = "select was not applied because groupBy is present; each output row holds the groupBy keys and aggregates.";
            }
            var aggregates = query.TryGetProperty("aggregates", out var aggregateArray) ? aggregateArray : default;
            if (groups.Count > 0) Require(aggregates.ValueKind == JsonValueKind.Array, "groupBy requires aggregates.");
            var invalidNumeric = new Dictionary<string, int>();
            List<JToken> rows;
            JsonElement? totals = null;
            if (aggregates.ValueKind != JsonValueKind.Undefined)
            {
                Require(aggregates.ValueKind == JsonValueKind.Array && aggregates.GetArrayLength() is > 0 and <= 12, "Provide 1-12 aggregates.");
                var specs = aggregates.EnumerateArray().Select(aggregate =>
                {
                    OnlyKeys(aggregate, "op", "path", "as");
                    var operation = String(aggregate, "op", "");
                    var alias = String(aggregate, "as", "");
                    Require(operation is "count" or "sum" or "avg" or "min" or "max", "Unsupported aggregate operation.");
                    Require(alias.Length is > 0 and <= 80 && !groups.ContainsKey(alias) && !invalidNumeric.ContainsKey(alias), "Aggregate names must be distinct from group names.");
                    invalidNumeric.Add(alias, 0);
                    return (Operation: operation, Alias: alias, Path: String(aggregate, "path", "$"));
                }).ToArray();
                var grouped = new Dictionary<string, (JObject Key, List<JToken> Rows)>(StringComparer.Ordinal);
                foreach (var row in selected)
                {
                    CheckBudget();
                    var key = Project(row, groups, CheckBudget, ReserveProjection);
                    Require(key.Properties().All(property => property.Value is JValue), "Group keys must be scalar values.");
                    var fingerprint = key.ToString(Newtonsoft.Json.Formatting.None);
                    if (!grouped.TryGetValue(fingerprint, out var group)) { group = (key, []); grouped.Add(fingerprint, group); }
                    group.Rows.Add(row);
                }
                if (selected.Length == 0 && groups.Count == 0) grouped.Add("{}", (new JObject(), []));
                rows = [];
                foreach (var group in grouped.Values)
                {
                    CheckBudget();
                    foreach (var spec in specs)
                    {
                        if (spec.Operation == "count") { group.Key[spec.Alias] = group.Rows.Count; continue; }
                        var numbers = new List<decimal>();
                        foreach (var row in group.Rows)
                        {
                            var value = Single(row, spec.Path, CheckBudget);
                            if (value is JValue scalar && scalar.Type is JTokenType.Integer or JTokenType.Float
                                && decimal.TryParse(scalar.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) numbers.Add(number);
                            else invalidNumeric[spec.Alias]++;
                        }
                        group.Key[spec.Alias] = numbers.Count == 0 ? JValue.CreateNull() : new JValue(spec.Operation switch
                        {
                            "sum" => numbers.Sum(),
                            "avg" => numbers.Average(),
                            "min" => numbers.Min(),
                            _ => numbers.Max()
                        });
                    }
                    rows.Add(group.Key);
                }
                if (projection.Count > 0)
                {
                    using (var totalsDocument = JsonDocument.Parse(rows.Single().ToString(Newtonsoft.Json.Formatting.None)))
                        totals = totalsDocument.RootElement.Clone();
                    rows = selected.Select(row => (JToken)Project(row, projection, CheckBudget, ReserveProjection)).ToList();
                }
            }
            else rows = selected.Select(row => projection.Count == 0 ? row : Project(row, projection, CheckBudget, ReserveProjection)).ToList();

            if (query.TryGetProperty("sort", out var sort))
            {
                Require(sort.ValueKind == JsonValueKind.Array && sort.GetArrayLength() is > 0 and <= 6, "Provide 1-6 sort keys.");
                IOrderedEnumerable<JToken>? ordered = null;
                foreach (var item in sort.EnumerateArray())
                {
                    OnlyKeys(item, "path", "direction");
                    var direction = String(item, "direction", "asc");
                    Require(direction is "asc" or "desc", "Sort direction must be asc or desc.");
                    var sortPath = String(item, "path", "$");
                    JToken? Key(JToken row) => Single(row, sortPath, CheckBudget);
                    ordered = ordered is null
                        ? direction == "asc" ? rows.OrderBy(Key, ValueComparer.Instance) : rows.OrderByDescending(Key, ValueComparer.Instance)
                        : direction == "asc" ? ordered.ThenBy(Key, ValueComparer.Instance) : ordered.ThenByDescending(Key, ValueComparer.Instance);
                }
                rows = ordered!.ToList();
            }
            var offset = Integer(query, "offset", 0, 0, 100000);
            var limit = Integer(query, "limit", 50, 0, 200);
            var page = new List<JsonElement>();
            var bytes = 0;
            foreach (var row in rows.Skip(offset).Take(limit))
            {
                CheckBudget();
                var text = row.ToString(Newtonsoft.Json.Formatting.None);
                var size = Encoding.UTF8.GetByteCount(text);
                if (bytes + size > 16000) break;
                using var rowDocument = JsonDocument.Parse(text);
                page.Add(rowDocument.RootElement.Clone()); bytes += size;
            }
            var complete = offset + page.Count >= rows.Count;
            return JsonSerializer.Serialize(new
            {
                resultId = entry.Id,
                source = entry.Source,
                sha256 = entry.Sha256,
                expiresUtc = entry.ExpiresUtc,
                totalMatches = selected.Length,
                totalResults = rows.Count,
                offset,
                returned = page.Count,
                complete,
                nextOffset = !complete && page.Count > 0 ? offset + page.Count : (int?)null,
                requiresProjection = limit > 0 && offset < rows.Count && page.Count == 0,
                invalidNumeric,
                totals,
                note,
                rows = page,
                guidance = "Counts and aggregates cover the entire retained selection before paging. Source metadata describes original API coverage; query complete describes this local result only. Missing/non-numeric aggregate values are counted, never silently zero-filled. Project narrower fields if requiresProjection is true."
            });
        }
        catch (QueryException exception) { return "Error: " + exception.Message; }
        catch (Exception exception) when (exception is JsonException or Newtonsoft.Json.JsonException or ArgumentException or InvalidOperationException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { return "Error: Invalid or over-budget JSON query. Use the returned schema and documented query parameters."; }
    }

    private sealed record Condition(string Path, string Operator, JsonElement Value)
    {
        internal bool Matches(JToken? token)
        {
            var actual = token is JValue { Value: not null } scalar ? scalar : null;
            string? Text(JToken? value) => value is JValue { Value: not null } item ? Convert.ToString(item.Value, CultureInfo.InvariantCulture) : null;
            bool Equal(JsonElement expected) => expected.ValueKind switch
            {
                JsonValueKind.Null => actual is null,
                JsonValueKind.Number => actual is not null && decimal.TryParse(Text(actual), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && expected.TryGetDecimal(out var target) && number == target,
                JsonValueKind.True or JsonValueKind.False => actual?.Type == JTokenType.Boolean && (bool)actual == expected.GetBoolean(),
                _ => string.Equals(Text(actual), expected.ToString(), StringComparison.OrdinalIgnoreCase)
            };
            int? Compare()
            {
                if (actual is null) return null;
                if (Value.ValueKind == JsonValueKind.Number && decimal.TryParse(Text(actual), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Value.TryGetDecimal(out var target))
                    return number.CompareTo(target);
                return Value.ValueKind == JsonValueKind.String ? string.CompareOrdinal(Text(actual), Value.GetString()) : null;
            }
            return Operator switch
            {
                "eq" => Equal(Value),
                "ne" => !Equal(Value),
                "in" => Value.EnumerateArray().Any(Equal),
                "notIn" => !Value.EnumerateArray().Any(Equal),
                "gt" => Compare() > 0,
                "gte" => Compare() >= 0,
                "lt" => Compare() < 0,
                "lte" => Compare() <= 0,
                "contains" => Text(actual)?.Contains(Value.GetString()!, StringComparison.OrdinalIgnoreCase) == true,
                "containsAny" => Text(actual) is { } text && Value.EnumerateArray().Any(item => text.Contains(item.GetString()!, StringComparison.OrdinalIgnoreCase)),
                "startsWith" => Text(actual)?.StartsWith(Value.GetString()!, StringComparison.OrdinalIgnoreCase) == true,
                "endsWith" => Text(actual)?.EndsWith(Value.GetString()!, StringComparison.OrdinalIgnoreCase) == true,
                "exists" => (token is not null && token.Type != JTokenType.Null) == Value.GetBoolean(),
                _ => false
            };
        }
    }

    private static Condition[] Conditions(JsonElement where)
    {
        Require(where.ValueKind == JsonValueKind.Array && where.GetArrayLength() is > 0 and <= 12, "where must be an array of 1-12 conditions.");
        return where.EnumerateArray().Select(item =>
        {
            Require(item.ValueKind == JsonValueKind.Object, "Each where condition must be an object.");
            OnlyKeys(item, "path", "op", "value");
            var path = String(item, "path", "$");
            var operation = String(item, "op", "eq");
            Require(item.TryGetProperty("value", out var value), "Each where condition requires a value.");
            var list = operation is "in" or "notIn" or "containsAny";
            var text = operation is "contains" or "startsWith" or "endsWith";
            Require(operation is "eq" or "ne" or "gt" or "gte" or "lt" or "lte" or "exists" || list || text, "Unsupported where operator. Use eq, ne, in, notIn, gt, gte, lt, lte, contains, containsAny, startsWith, endsWith or exists.");
            if (list) Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 and <= 100
                && value.EnumerateArray().All(entry => operation != "containsAny" || entry.ValueKind == JsonValueKind.String && entry.GetString()!.Length is > 0 and <= 200), "List operators need an array of 1-100 values (non-empty strings for containsAny).");
            if (text) Require(value.ValueKind == JsonValueKind.String && value.GetString()!.Length is > 0 and <= 200, "Text operators need a non-empty string of at most 200 characters.");
            if (operation == "exists") Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "exists needs true or false.");
            if (!list && !text && operation != "exists") Require(value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null, "Comparison values must be scalars.");
            return new Condition(path, operation, value.Clone());
        }).ToArray();
    }

    private static IEnumerable<JToken> Select(JToken root, string path, Action check)
    {
        Require(path.Length is > 0 and <= 1024 && path[0] == '$', "JSONPath must start with $ and contain at most 1024 characters.");
        Require(!path.Contains("..", StringComparison.Ordinal) && !path.Contains("=~", StringComparison.Ordinal), "Use explicit schema paths; recursive descent and regex predicates are not supported. Filter rows with where, e.g. [{\"path\":\"$[1]\",\"op\":\"containsAny\",\"value\":[\"virtualMachines\",\"managedClusters\"]}].");
        var count = 0;
        foreach (var value in root.SelectTokens(path, new JsonSelectSettings { RegexMatchTimeout = TimeSpan.FromMilliseconds(100), ErrorWhenNoMatch = false }))
        {
            check();
            Require(++count <= 100000, "Selection exceeds 100000 matches; narrow the selector.");
            yield return value;
        }
    }

    private static JToken? Single(JToken row, string path, Action check)
    {
        var values = Select(row, path, check).Take(2).ToArray();
        Require(values.Length <= 1, "Per-row field paths must select one value; use an array path to preserve nested arrays.");
        return values.FirstOrDefault();
    }

    private static JObject Project(JToken row, Dictionary<string, string> paths, Action check, Action<int> reserve)
    {
        var result = new JObject();
        foreach (var field in paths)
        {
            var value = Single(row, field.Value, check);
            var length = value?.ToString(Newtonsoft.Json.Formatting.None).Length ?? 0;
            reserve(Math.Min(length, 16000));
            result[field.Key] = length <= 16000
                ? value?.DeepClone() ?? JValue.CreateNull()
                : new JObject
                {
                    ["omitted"] = "Field exceeds the 16000-character page budget; query its child path directly.",
                    ["type"] = value!.Type.ToString().ToLowerInvariant(),
                    ["items"] = value is JArray array ? array.Count : null,
                    ["characters"] = length
                };
        }
        return result;
    }

    private static Dictionary<string, string> Paths(JsonElement query, string name, int max)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!query.TryGetProperty(name, out var element)) return paths;
        Require(element.ValueKind == JsonValueKind.Object, "Field selections must be JSON objects.");
        foreach (var property in element.EnumerateObject())
        {
            Require(property.Name.Length is > 0 and <= 80 && property.Value.ValueKind == JsonValueKind.String && !paths.ContainsKey(property.Name), "Field names and paths must be distinct strings.");
            paths.Add(property.Name, property.Value.GetString()!);
        }
        Require(paths.Count is > 0 && paths.Count <= max, "Field selection count exceeds its limit.");
        return paths;
    }

    private static void OnlyKeys(JsonElement value, params string[] allowed)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Query operation must be an object.");
        var names = new HashSet<string>();
        foreach (var property in value.EnumerateObject()) Require(allowed.Contains(property.Name) && names.Add(property.Name), "Unknown or duplicate operation parameter.");
    }
    private static string String(JsonElement value, string key, string fallback)
    {
        if (!value.TryGetProperty(key, out var item)) return fallback;
        Require(item.ValueKind == JsonValueKind.String, "Query string parameter has invalid type.");
        return item.GetString()!;
    }
    private static int Integer(JsonElement value, string key, int fallback, int minimum, int maximum)
    {
        if (!value.TryGetProperty(key, out var item)) return fallback;
        Require(item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _), "Query paging parameter must be an integer.");
        var number = item.GetInt32();
        Require(number >= minimum && number <= maximum, "Query paging parameter is out of range.");
        return number;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new QueryException(message); }
    private sealed class QueryException(string message) : Exception(message);
    private sealed class ValueComparer : IComparer<JToken?>
    {
        internal static ValueComparer Instance { get; } = new();
        public int Compare(JToken? left, JToken? right)
        {
            if (left is null || left.Type == JTokenType.Null) return right is null || right.Type == JTokenType.Null ? 0 : -1;
            if (right is null || right.Type == JTokenType.Null) return 1;
            if (left is JValue leftValue && right is JValue rightValue) return leftValue.CompareTo(rightValue);
            throw new QueryException("Sort keys must be scalar values.");
        }
    }
}