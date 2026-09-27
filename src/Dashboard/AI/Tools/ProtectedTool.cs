using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;
using GitHub.Copilot;

namespace AzureFinOps.Dashboard.AI.Tools;

internal sealed class ProtectedTool(AIFunction inner, long? owner = null, string? sessionId = null) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        UnwrapCallEnvelope(Name, arguments);
        CoerceScalarStrings(JsonSchema, arguments);
        var argumentJson = JsonSerializer.Serialize(arguments);
        if (SensitiveContent.ContainsSecret(argumentJson))
            return SensitiveContent.RejectedMessage;
        var scopeKey = CostQueryCoordinator.RequestKey("", Name, "", argumentJson);
        TurnExecution? turn = null;
        var invocation = arguments.Services?.GetService(typeof(ToolInvocation)) as ToolInvocation
            ?? arguments.Context?.Values.OfType<ToolInvocation>().FirstOrDefault();
        if (sessionId is not null && (invocation?.SessionId != sessionId || invocation.ToolName != Name || string.IsNullOrEmpty(invocation.ToolCallId)))
            throw new OperationCanceledException("The host invocation identity could not be verified.");
        if (sessionId is not null && (!TurnExecution.Active.TryGetValue(sessionId, out turn) || owner is null))
            throw new OperationCanceledException("The originating turn is not active.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turn?.CancellationToken ?? CancellationToken.None);
        using var lease = turn is null ? null : await turn.AcquireToolAsync(owner!.Value, invocation!.ToolCallId, linked.Token);
        using var context = new ToolExecutionContext(sessionId, owner, linked.Token) { ToolCallId = invocation?.ToolCallId };
        linked.Token.ThrowIfCancellationRequested();
        // sql without url reads this conversation's stored responses; it calls no API, so it is never fresh evidence.
        if (Name == "QueryAzure" && string.IsNullOrWhiteSpace(Argument(arguments, "url")) && Argument(arguments, "sql") is { Length: > 0 } sql
            && !string.IsNullOrWhiteSpace(sql))
        {
            var rows = owner is null || sessionId is null
                ? "Error: no active conversation."
                : SensitiveContent.Redact(ResultDatabase.For(owner.Value, sessionId).Query(sql, null, linked.Token));
            turn?.RecordTool(!rows.StartsWith("Error", StringComparison.Ordinal));
            return rows;
        }
        object? result;
        try { result = await base.InvokeCoreAsync(arguments, linked.Token); }
        catch
        {
            turn?.RecordTool(false);
            turn?.ToolEvidence.Enqueue(new(Name, false, false, false, DateTimeOffset.UtcNow, scopeKey));
            throw;
        }
        var resultText = result?.ToString() ?? "";
        var evidence = InspectEvidence(resultText);
        if (EvidenceTools.Contains(Name)) turn?.ToolEvidence.Enqueue(new(Name, evidence.Success, evidence.Fresh, evidence.Partial, DateTimeOffset.UtcNow, scopeKey));
        turn?.RecordTool(evidence.Success);
        if (evidence.Success && Name is "RenderChart" or "RenderAdvancedChart" or "ReportMaturityScore")
            turn?.RecordVisibleOutput();
        if (resultText.StartsWith("__HTML_READY__:") || resultText.StartsWith("__SCRIPT_READY__:"))
        {
            var identifier = resultText.Split(':').ElementAtOrDefault(1);
            if (owner is not null && identifier is not null && ArtifactStore.Default.Find(identifier, owner.Value) is not null) turn?.ArtifactIds.Enqueue(identifier);
        }
        if (result is string text) return PrepareResult(text, evidence, arguments, linked.Token);
        if (result is JsonElement { ValueKind: JsonValueKind.String } scalar)
            return JsonSerializer.SerializeToElement(PrepareResult(scalar.GetString() ?? "", evidence, arguments, linked.Token));
        if (result is JsonElement element)
        {
            var prepared = PrepareResult(element.GetRawText(), evidence, arguments, linked.Token);
            try { return JsonSerializer.Deserialize<JsonElement>(prepared); }
            catch (JsonException) { return JsonSerializer.SerializeToElement(prepared); }
        }
        return result;
    }

    // Models occasionally serialize a parallel call as {"recipient_name":"functions.<tool>","parameters":{...}}.
    // Unwrap only that exact envelope, and only when it names the tool the SDK actually invoked.
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

    // Evidence responses are stored whole per conversation; the model receives small ones in full, large ones as
    // their shape, or only the rows its sql selects. Failures and operation envelopes stay verbatim.
    private string PrepareResult(string text, (bool Success, bool Fresh, bool Partial) evidence, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var redacted = SensitiveContent.Redact(text);
        if (owner is null || sessionId is null || !EvidenceTools.Contains(Name) || !evidence.Success
            || redacted.Contains("__HTML_READY__:", StringComparison.Ordinal) || redacted.Contains("__SCRIPT_READY__:", StringComparison.Ordinal)
            || redacted.Contains("\"operationId\"", StringComparison.Ordinal)) return redacted;
        var azure = Name == "QueryAzure";
        return ResultDatabase.For(owner.Value, sessionId).Present(
            azure ? Argument(arguments, "url") ?? "" : "upload:" + (Argument(arguments, "fileId") ?? "") + "/" + (Argument(arguments, "mode") ?? ""),
            azure ? Argument(arguments, "method") ?? "GET" : Name,
            redacted, azure ? Argument(arguments, "sql") : null, cancellationToken);
    }

    private static string? Argument(AIFunctionArguments arguments, string name) =>
        arguments.TryGetValue(name, out var value) ? value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array } element => element.GetRawText(),
            null => null,
            var other => other.ToString()
        } : null;

    private static readonly HashSet<string> EvidenceTools = ["QueryAzure", "QueryUploadedFile"];

    internal static (bool Success, bool Fresh, bool Partial) InspectEvidence(string text)
    {
        var success = !string.IsNullOrWhiteSpace(text) && !text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("HTTP 4", StringComparison.Ordinal) && !text.StartsWith("HTTP 5", StringComparison.Ordinal);
        var fresh = !text.Contains("stale_during_cooldown", StringComparison.Ordinal);
        var partial = text.Contains("PARTIAL RESULT", StringComparison.Ordinal);

        void Inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Inspect(item);
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
                    if (value.GetString() is "accepted" or "inProgress" or "awaitingApproval" or "dispatching" or "unknown" or "partial" or "ambiguous" or "missing") partial = true;
                }
                if (property.Name is "_finops" or "sourceEvidence" or "results" or "resolution" or "coverage" or "rows" or "result" or "body") Inspect(value);
            }
        }

        var body = text.StartsWith("HTTP ") ? text[(text.IndexOf('\n') + 1)..] : text;
        if (body.StartsWith("Current UTC time: ", StringComparison.Ordinal))
        {
            var timestampEnd = body.IndexOf('\n');
            body = timestampEnd >= 0 ? body[(timestampEnd + 1)..] : "";
        }
        try { using var document = JsonDocument.Parse(body); Inspect(document.RootElement); }
        catch (JsonException)
        {
            foreach (var line in body.Split('\n').Where(line => line.StartsWith("RESOLUTION", StringComparison.Ordinal)))
            {
                var start = line.IndexOf('{');
                if (start < 0) continue;
                try { using var document = JsonDocument.Parse(line[start..]); Inspect(document.RootElement); }
                catch (JsonException) { partial = true; }
            }
        }
        return (success, fresh, partial);
    }
}