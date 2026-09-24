using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;
using GitHub.Copilot;

namespace AzureFinOps.Dashboard.AI.Tools;

internal sealed class ProtectedTool(AIFunction inner, long? owner = null, string? sessionId = null) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
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
        if (evidence.Success && Name is "RenderChart" or "RenderAdvancedChart" or "GetCrawlMaturityEvidence" or "ReportMaturityScore")
            turn?.RecordVisibleOutput();
        if (resultText.StartsWith("__HTML_READY__:") || resultText.StartsWith("__SCRIPT_READY__:"))
        {
            var identifier = resultText.Split(':').ElementAtOrDefault(1);
            if (owner is not null && identifier is not null && ArtifactStore.Default.Find(identifier, owner.Value) is not null) turn?.ArtifactIds.Enqueue(identifier);
        }
        if (result is string text) return PrepareResult(text, evidence);
        if (result is JsonElement { ValueKind: JsonValueKind.String } scalar)
            return JsonSerializer.SerializeToElement(PrepareResult(scalar.GetString() ?? "", evidence));
        if (result is JsonElement element)
            return JsonSerializer.Deserialize<JsonElement>(PrepareResult(element.GetRawText(), evidence));
        return result;
    }

    private string PrepareResult(string text, (bool Success, bool Fresh, bool Partial) evidence)
    {
        var redacted = SensitiveContent.Redact(text);
        if (owner is null || sessionId is null || !evidence.Success || !EvidenceTools.Contains(Name)
            || Name is "GetCrawlMaturityEvidence"
            || redacted.Contains("__HTML_READY__:", StringComparison.Ordinal) || redacted.Contains("__SCRIPT_READY__:", StringComparison.Ordinal)) return redacted;
        var large = System.Text.Encoding.UTF8.GetByteCount(redacted) > ToolResultStore.InlineBytes;
        if (Name == "BulkAzureRequest" && large && redacted.Contains("\"operationId\"", StringComparison.Ordinal)) return redacted;
        var entry = ToolResultStore.Default.Retain(owner.Value, sessionId, redacted,
            new(Name, DateTimeOffset.UtcNow, evidence.Success, evidence.Fresh, evidence.Partial, ""));
        if (entry is null) return redacted;
        return large ? JsonSerializer.Serialize(ToolResultStore.Describe(entry)) : ToolResultStore.AnnotateInline(redacted, entry);
    }

    private static readonly HashSet<string> EvidenceTools = new(StringComparer.Ordinal)
{
    "QueryAzure",
    "QueryGraph",
    "GetCopilotUsage",
    "QueryLogAnalytics",
    "QueryCostsAcrossSubscriptions",
    "GetCrawlMaturityEvidence",
    "GetTagCoverage",
    "BulkAzureRequest",
    "FindIdleResources",
    "DetectCostAnomalies",
    "GetAzureRetailPricing",
    "GetAzureRetailPricingBatch",
    "QueryUploadedFile",
    "ReadCostExportBlob",
    "ListCostExportBlobs",
    "FetchPublicWebPage",
    "CheckComputeFeasibility",
    "CheckVmConnectivity",
    "GetOperationStatus"
};

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
                if (property.Name is "skuStatus" or "quotaStatus" && value.ValueKind == JsonValueKind.String && value.GetString() == "unknown") partial = true;
                if (property.Name is "status" or "outcome" && value.ValueKind == JsonValueKind.String)
                {
                    if (value.GetString() is "failed" or "cancelled") success = false;
                    if (value.GetString() is "accepted" or "inProgress" or "awaitingApproval" or "dispatching" or "unknown" or "partial" or "ambiguous" or "missing") partial = true;
                }
                if (property.Name is "_finops" or "sourceEvidence" or "results" or "resolution" or "coverage" or "spotPlacement" or "rows" or "result" or "body") Inspect(value);
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