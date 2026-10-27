using System.Text.Json;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>Reads success, freshness and coverage from a tool result's own text, so outcomes and scheduled-run
/// validation rely on what the source returned rather than on what the model claims.</summary>
internal static class EvidenceInspector
{
    internal static readonly HashSet<string> EvidenceTools = ["QueryAzure", "QueryUploadedFile"];

    internal static (bool Success, bool Fresh, bool Partial) Inspect(string text)
    {
        var success = !string.IsNullOrWhiteSpace(text) && !text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("HTTP 4", StringComparison.Ordinal) && !text.StartsWith("HTTP 5", StringComparison.Ordinal);
        var fresh = !text.Contains("stale_during_cooldown", StringComparison.Ordinal);
        var partial = text.Contains("PARTIAL RESULT", StringComparison.Ordinal) || text.Contains("\"complete\":false", StringComparison.Ordinal);

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item);
                return;
            }
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value;
                if (property.Name is "ok" && value.ValueKind == JsonValueKind.False) success = false;
                if (property.Name is "error" && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) success = false;
                if (property.Name is "complete" && value.ValueKind == JsonValueKind.False) partial = true;
                if (property.Name is "partial" && value.ValueKind == JsonValueKind.True) partial = true;
                if (property.Name == "source" && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("Tool", out _))
                {
                    if (value.TryGetProperty("Success", out var sourceSuccess) && sourceSuccess.ValueKind == JsonValueKind.False) success = false;
                    if (value.TryGetProperty("Fresh", out var sourceFresh) && sourceFresh.ValueKind == JsonValueKind.False) fresh = false;
                    if (value.TryGetProperty("Partial", out var sourcePartial) && sourcePartial.ValueKind == JsonValueKind.True) partial = true;
                }
                if (property.Name is "cacheStatus" && value.ValueKind == JsonValueKind.String && value.GetString() is not "queried") fresh = false;
                if (property.Name is "freshness" && value.ValueKind == JsonValueKind.String && value.GetString() is "unknown" or "periodic") fresh = false;
                if (property.Name is "status" or "outcome" && value.ValueKind == JsonValueKind.String)
                {
                    if (value.GetString() is "failed" or "cancelled") success = false;
                    if (value.GetString() is "accepted" or "inProgress" or "dispatching" or "unknown" or "partial" or "ambiguous" or "missing") partial = true;
                }
                if (property.Name is "_finops" or "sourceEvidence" or "results" or "resolution" or "coverage" or "rows" or "result" or "body") Visit(value);
            }
        }

        // 202 Accepted means the source has not finished; the result is not complete evidence.
        if (text.StartsWith("HTTP 202 ", StringComparison.Ordinal)) partial = true;
        var body = Infrastructure.ResponseShaper.SplitPreamble(text).Body;
        try
        {
            using var document = JsonDocument.Parse(body);
            // An MCP tool result (Microsoft Learn) reports its own failure as a root isError; a row or column of that
            // name in API data is not a failure.
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("isError", out var isError)
                && isError.ValueKind == JsonValueKind.True) success = false;
            Visit(document.RootElement);
        }
        catch (JsonException)
        {
            // Line-oriented results (fan-out, cropped query results, schemas) carry their provenance on RESOLUTION or Coverage lines.
            foreach (var line in body.Split('\n').Where(line => line.StartsWith("RESOLUTION", StringComparison.Ordinal) || line.StartsWith("Coverage: ", StringComparison.Ordinal)))
            {
                var start = line.IndexOf('{');
                if (start < 0) continue;
                try { using var document = JsonDocument.Parse(line[start..]); Visit(document.RootElement); }
                catch (JsonException) { partial = true; }
            }
        }
        return (success, fresh, partial);
    }
}
