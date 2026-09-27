using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

internal sealed partial class ProtectedTool(AIFunction inner, long? owner = null, string? sessionId = null) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        TurnExecution? turn = null;
        var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId;
        if (sessionId is not null && (string.IsNullOrEmpty(callId) || owner is null
            || !TurnExecution.Active.TryGetValue(sessionId, out turn) || turn.Session?.SessionId != sessionId))
            throw new OperationCanceledException("The originating turn is not active.");
        var conversation = turn?.Session;
        UnwrapCallEnvelope(Name, arguments);
        CoerceScalarStrings(JsonSchema, arguments);
        if (conversation is not null)
            await conversation.ToolStartedAsync(callId!, Name, SensitiveContent.Redact(JsonSerializer.Serialize(arguments)));
        try
        {
            var result = await InvokeGuardedAsync(arguments, turn, callId, cancellationToken);
            if (conversation is not null)
                await conversation.ToolCompletedAsync(callId!, true, ResultText(result), null);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The model sees the redacted failure as the tool result, as with any "Error:" result.
            var error = SensitiveContent.Redact(exception.Message);
            if (conversation is not null)
                await conversation.ToolCompletedAsync(callId!, false, null, error);
            return $"Error: {error}";
        }
        catch when (conversation is not null)
        {
            await conversation.ToolCompletedAsync(callId!, false, null, "The tool was cancelled.");
            throw;
        }
    }

    private static string? ResultText(object? result) => result switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } scalar => scalar.GetString(),
        JsonElement element => element.GetRawText(),
        var other => other.ToString()
    };

    private async ValueTask<object?> InvokeGuardedAsync(AIFunctionArguments arguments, TurnExecution? turn, string? callId, CancellationToken cancellationToken)
    {
        var argumentJson = JsonSerializer.Serialize(arguments);
        if (SensitiveContent.ContainsSecret(argumentJson))
            return SensitiveContent.RejectedMessage;
        var scopeKey = CostQueryCoordinator.RequestKey("", Name, "", argumentJson);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turn?.CancellationToken ?? CancellationToken.None);
        using var lease = turn?.AcquireTool(owner!.Value);
        using var context = new ToolExecutionContext(sessionId, owner, linked.Token) { ToolCallId = callId };
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
        if (Name == "QueryAzure" && Urls(Argument(arguments, "url")) is { Length: > 0 } urls)
        {
            if (urls.Length > 1) return await FanOutAsync(urls, arguments, turn, linked.Token);
            arguments["url"] = urls[0];
        }
        try
        {
            var (result, success) = await InvokeOneAsync(arguments, turn, scopeKey, null, linked.Token);
            turn?.RecordTool(success);
            return result;
        }
        catch
        {
            turn?.RecordTool(false);
            throw;
        }
    }

    internal const int MaxUrls = 50;
    private const int UrlConcurrency = 4;
    private const int FanOutCharacters = 128 * 1024;

    // Several GET urls (one per line, or a JSON array of strings) run as one tool call, at most 4 at a time.
    internal static string[] Urls(string? url)
    {
        var text = url?.Trim() ?? "";
        if (text.StartsWith('['))
        {
            try
            {
                var items = JsonSerializer.Deserialize<string[]>(text);
                if (items is not null && items.All(item => item is not null)) return items.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
            }
            catch (JsonException) { }
        }
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<string> FanOutAsync(string[] urls, AIFunctionArguments arguments, TurnExecution? turn, CancellationToken cancellationToken)
    {
        var method = Argument(arguments, "method");
        if (urls.Length > MaxUrls || !string.IsNullOrWhiteSpace(method) && !method.Trim().Equals("GET", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Argument(arguments, "body"))
            || urls.Any(url => url.StartsWith("operation:", StringComparison.OrdinalIgnoreCase)))
        {
            turn?.RecordTool(false);
            return $"HTTP 400 BadRequest\nSeveral urls (one per line) are GET only, without body or operation:, and at most {MaxUrls} per call. No request was sent.";
        }
        var shapes = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(UrlConcurrency);
        var outputs = await Task.WhenAll(urls.Select(async url =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var item = new AIFunctionArguments(arguments) { Services = arguments.Services, Context = arguments.Context };
                item["url"] = url;
                var scopeKey = CostQueryCoordinator.RequestKey("", Name, "", JsonSerializer.Serialize(item));
                var (result, success) = await InvokeOneAsync(item, turn, scopeKey, shapes, cancellationToken);
                return (Text: ResultText(result) ?? "", Success: success);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return (Text: "Error: " + SensitiveContent.Redact(exception.Message), Success: false);
            }
            finally { gate.Release(); }
        }));
        var failed = outputs.Count(output => !output.Success);
        turn?.RecordTool(failed == 0);
        var text = new StringBuilder(failed == urls.Length ? $"Error: all {urls.Length} requests failed.\n"
            : failed > 0 ? $"PARTIAL RESULT: {failed} of {urls.Length} requests failed; a failed request is unknown, not empty.\n"
            : $"{urls.Length} requests succeeded; results follow in url order.\n");
        for (var index = 0; index < urls.Length; index++)
        {
            var output = outputs[index].Text.TrimEnd();
            if (text.Length + output.Length > FanOutCharacters)
                output = (StoredId().Match(output) is { Success: true } stored ? stored.Value + " " : output[..Math.Min(300, output.Length)] + " ")
                    + "Output omitted to keep the combined result small; read it with sql by id.";
            text.Append('[').Append(index + 1).Append("] ").Append(urls[index]).Append('\n').Append(output).Append("\n\n");
        }
        return text.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\[Stored as responses\.id = \d+\.\]")]
    private static partial Regex StoredId();

    private async ValueTask<(object? Result, bool Success)> InvokeOneAsync(AIFunctionArguments arguments, TurnExecution? turn, string scopeKey,
        ConcurrentDictionary<string, long>? shapes, CancellationToken cancellationToken)
    {
        object? result;
        try { result = await base.InvokeCoreAsync(arguments, cancellationToken); }
        catch
        {
            turn?.ToolEvidence.Enqueue(new(Name, false, false, false, DateTimeOffset.UtcNow, scopeKey));
            throw;
        }
        var resultText = result?.ToString() ?? "";
        var evidence = InspectEvidence(resultText);
        if (EvidenceTools.Contains(Name)) turn?.ToolEvidence.Enqueue(new(Name, evidence.Success, evidence.Fresh, evidence.Partial, DateTimeOffset.UtcNow, scopeKey));
        if (evidence.Success && Name is "RenderChart" or "RenderAdvancedChart" or "ReportMaturityScore")
            turn?.RecordVisibleOutput();
        if (resultText.StartsWith("__HTML_READY__:") || resultText.StartsWith("__SCRIPT_READY__:"))
        {
            var identifier = resultText.Split(':').ElementAtOrDefault(1);
            if (owner is not null && identifier is not null && ArtifactStore.Default.Find(identifier, owner.Value) is not null) turn?.ArtifactIds.Enqueue(identifier);
        }
        if (result is string text) return (PrepareResult(text, evidence, arguments, shapes, cancellationToken), evidence.Success);
        if (result is JsonElement { ValueKind: JsonValueKind.String } scalar)
            return (JsonSerializer.SerializeToElement(PrepareResult(scalar.GetString() ?? "", evidence, arguments, shapes, cancellationToken)), evidence.Success);
        if (result is JsonElement element)
        {
            var prepared = PrepareResult(element.GetRawText(), evidence, arguments, shapes, cancellationToken);
            try { return (JsonSerializer.Deserialize<JsonElement>(prepared), evidence.Success); }
            catch (JsonException) { return (JsonSerializer.SerializeToElement(prepared), evidence.Success); }
        }
        return (result, evidence.Success);
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
    private string PrepareResult(string text, (bool Success, bool Fresh, bool Partial) evidence, AIFunctionArguments arguments,
        ConcurrentDictionary<string, long>? shapes, CancellationToken cancellationToken)
    {
        var redacted = SensitiveContent.Redact(text);
        if (owner is null || sessionId is null || !EvidenceTools.Contains(Name) || !evidence.Success
            || redacted.Contains("__HTML_READY__:", StringComparison.Ordinal) || redacted.Contains("__SCRIPT_READY__:", StringComparison.Ordinal)
            || redacted.Contains("\"operationId\"", StringComparison.Ordinal)) return redacted;
        var azure = Name == "QueryAzure";
        return ResultDatabase.For(owner.Value, sessionId).Present(
            azure ? Argument(arguments, "url") ?? "" : "upload:" + (Argument(arguments, "fileId") ?? "") + "/" + (Argument(arguments, "mode") ?? ""),
            azure ? Argument(arguments, "method") ?? "GET" : Name,
            redacted, azure ? Argument(arguments, "sql") : null, cancellationToken, shapes);
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