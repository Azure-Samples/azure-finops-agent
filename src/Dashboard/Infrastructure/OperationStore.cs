using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzureFinOps.Dashboard.Infrastructure;

internal sealed class OperationStore
{
    internal sealed record Operation(string Id, long Owner, string SessionId, string Method, string ResourceUrl,
        string PollUrl, string Fingerprint, string Status, DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc,
        DateTimeOffset NextPollUtc, JsonElement? Details, string? PendingBody = null, DateTimeOffset? ApprovedUtc = null);
    internal static OperationStore Default { get; } = new(Path.Combine(
        Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(Path.GetTempPath(), "copilot"), "operations"));
    private readonly string _root;
    private readonly object _sync = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCleanupUtc;

    internal OperationStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                var operation = JsonSerializer.Deserialize<Operation>(File.ReadAllText(path));
                if (operation is not null && Regex.IsMatch(operation.Id, "^[a-f0-9]{32}$") && IsArmUrl(operation.ResourceUrl)
                    && IsArmUrl(operation.PollUrl))
                    _operations[operation.Id] = operation.Status == "dispatching" ? operation with { Status = "unknown" } : operation;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { }
        }
        Cleanup();
    }

    internal static bool IsArmUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    internal Operation? Find(string id, long owner)
    {
        lock (_sync)
        {
            Cleanup();
            return _operations.TryGetValue(id, out var value) && value.Owner == owner ? value : null;
        }
    }

    internal Operation? Pending(long owner, string method, string url, string? body)
    {
        var fingerprint = Fingerprint(method, url, body);
        lock (_sync) return _operations.Values.FirstOrDefault(operation => operation.Owner == owner && operation.Fingerprint == fingerprint
            && operation.Status is "awaitingApproval" or "dispatching" or "accepted" or "inProgress" or "unknown"
            && (operation.Status != "awaitingApproval" || operation.StartedUtc > DateTimeOffset.UtcNow.AddMinutes(-30)));
    }

    internal Operation Begin(long owner, string sessionId, string method, string url, string? body, out bool created, bool requiresApproval = false)
    {
        if (!IsArmUrl(url)) throw new InvalidOperationException("Invalid operation resource.");
        if (SensitiveContent.ContainsSecret(body)) throw new InvalidOperationException(SensitiveContent.RejectedMessage);
        lock (_sync)
        {
            Cleanup();
            if (Pending(owner, method, url, body) is { } pending) { created = false; return pending; }
            var now = DateTimeOffset.UtcNow;
            var operation = new Operation(Guid.NewGuid().ToString("N"), owner, sessionId, method, url, url,
                Fingerprint(method, url, body), requiresApproval ? "awaitingApproval" : "dispatching", now, now, now.AddSeconds(5), null,
                requiresApproval ? body : null);
            Save(operation);
            created = true;
            return operation;
        }
    }

    internal Operation? Approve(string id, long owner, string sessionId)
    {
        lock (_sync)
        {
            var operation = Find(id, owner);
            if (operation is null || operation.SessionId != sessionId || operation.Status != "awaitingApproval"
                || operation.StartedUtc < DateTimeOffset.UtcNow.AddMinutes(-30)) return null;
            var approved = operation with { Status = "dispatching", ApprovedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow };
            Save(approved);
            return approved;
        }
    }

    internal bool Reject(string id, long owner)
    {
        lock (_sync)
        {
            if (Find(id, owner) is not { Status: "awaitingApproval" } operation) return false;
            Save(operation with { Status = "rejected", PendingBody = null, UpdatedUtc = DateTimeOffset.UtcNow });
            return true;
        }
    }

    internal bool MatchesApproved(Operation operation, long owner, string sessionId, string method, string url, string? body) =>
        operation.Owner == owner && operation.SessionId == sessionId && operation.Status == "dispatching" && operation.ApprovedUtc is not null
        && operation.Fingerprint == Fingerprint(method, url, body);

    internal static object Review(Operation operation) => new
    {
        operationId = operation.Id, method = operation.Method, target = new Uri(operation.ResourceUrl).AbsolutePath,
        body = operation.PendingBody, status = operation.Status, expiresUtc = operation.StartedUtc.AddMinutes(30),
        costImpact = "Not independently verified. Review the configuration and potential charges before approving."
    };

    internal Operation MarkUncertain(Operation operation)
    {
        var updated = operation with { Status = "unknown", UpdatedUtc = DateTimeOffset.UtcNow };
        Save(updated);
        return updated;
    }

    internal IReadOnlyList<Operation> ForSession(long owner, string sessionId)
    {
        lock (_sync)
        {
            Cleanup();
            return _operations.Values.Where(operation => operation.Owner == owner && operation.SessionId == sessionId)
                .OrderByDescending(operation => operation.StartedUtc).Take(100).ToArray();
        }
    }

    internal Operation Register(long owner, string sessionId, string method, string url, string? requestBody, HttpResponseMessage response, string responseBody)
    {
        var operation = Begin(owner, sessionId, method, url, requestBody, out _);
        return ObserveResponse(operation, response, responseBody);
    }

    internal Operation ObserveResponse(Operation operation, HttpResponseMessage response, string responseBody, bool polling = false)
    {
        var pollUrl = response.Headers.TryGetValues("Azure-AsyncOperation", out var asyncUrls) ? asyncUrls.FirstOrDefault()
            : response.Headers.Location?.ToString();
        if (!IsArmUrl(pollUrl)) pollUrl = operation.PollUrl;
        var now = DateTimeOffset.UtcNow;
        var details = ParseDetails(responseBody);
        var status = Classify((int)response.StatusCode, details);
        var code = (int)response.StatusCode;
        if (polling && code is < 200 or >= 300 || code >= 500) status = "unknown";
        var seconds = Math.Min(HttpHelper.ResolveRetryAfterSeconds(response, 0), (DateTimeOffset.MaxValue - now).TotalSeconds - 1);
        var updated = operation with { PollUrl = pollUrl!, Status = status, UpdatedUtc = now,
            NextPollUtc = now.AddSeconds(Math.Max(1, seconds)), Details = details, PendingBody = null };
        Save(updated);
        return updated;
    }

    internal Operation Update(Operation operation, int statusCode, string response)
    {
        using var message = new HttpResponseMessage((System.Net.HttpStatusCode)statusCode);
        return ObserveResponse(operation, message, response, polling: true);
    }

    internal static string Classify(int statusCode, JsonElement? details)
    {
        var state = "";
        if (details is { ValueKind: JsonValueKind.Object } value)
        {
            if (value.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return "failed";
            if (value.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String) state = status.GetString() ?? "";
            if (value.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("provisioningState", out var provisioning) && provisioning.ValueKind == JsonValueKind.String) state = provisioning.GetString() ?? state;
        }
        if (state.Equals("Failed", StringComparison.OrdinalIgnoreCase) || state.Equals("Canceled", StringComparison.OrdinalIgnoreCase) || state.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return "failed";
        if (statusCode is < 200 or >= 300) return "failed";
        if (state.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)) return "succeeded";
        if (statusCode == 202) return "accepted";
        if (state.Length > 0) return "inProgress";
        return statusCode is 200 or 204 ? "succeeded" : "inProgress";
    }

    internal static string Envelope(Operation operation, bool duplicate = false) => JsonSerializer.Serialize(new
    {
        operationId = operation.Id, status = operation.Status, ok = operation.Status == "succeeded",
        method = operation.Method, resource = new Uri(operation.ResourceUrl).AbsolutePath,
        duplicateSuppressed = duplicate, nextPollUtc = operation.NextPollUtc,
        startedUtc = operation.StartedUtc, updatedUtc = operation.UpdatedUtc,
        details = operation.Details,
        code = operation.Status == "awaitingApproval" ? "approval_required" : null,
        nextAction = operation.Status == "awaitingApproval" ? "No write was sent. The user must review and approve the exact change in the application."
            : operation.Status is "dispatching" or "accepted" or "inProgress" or "unknown"
            ? "Use GetOperationStatus with this operationId. Do not re-submit the write or claim completion."
            : operation.Status == "failed" ? "Report the failure and list any created prerequisite resources. Generate a reviewed cleanup script; never delete automatically."
            : operation.Status == "expired" ? "The approval proposal expired. No write was sent. Request a fresh plan."
            : operation.Status == "rejected" ? "The user rejected this change. No write was sent." : "Terminal success observed."
    });

    private void Cleanup()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastCleanupUtc < TimeSpan.FromMinutes(1)) return;
            _lastCleanupUtc = now;
            foreach (var operation in _operations.Values.ToArray())
            {
                try
                {
                    if (operation.Status == "awaitingApproval" && operation.StartedUtc <= now.AddMinutes(-30))
                        Save(operation with { Status = "expired", PendingBody = null, UpdatedUtc = now });
                    else if (operation.Status is "succeeded" or "failed" or "rejected" or "expired" && operation.UpdatedUtc < now.AddDays(-7))
                    {
                        File.Delete(Path.Combine(_root, operation.Id + ".json"));
                        _operations.Remove(operation.Id);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    HttpHelper.Logger?.LogWarning("Operation cleanup failed: {ErrorType}", exception.GetType().Name);
                }
            }
        }
    }

    private void Save(Operation operation)
    {
        lock (_sync)
        {
            var path = Path.Combine(_root, operation.Id + ".json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(operation));
            File.Move(path + ".tmp", path, true);
            _operations[operation.Id] = operation;
        }
    }

    private static string Fingerprint(string method, string url, string? body) => CostQueryCoordinator.RequestKey("", method, url, body);
    private static JsonElement? ParseDetails(string content)
    {
        try
        {
            using var parsed = JsonDocument.Parse(SensitiveContent.Redact(content));
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var retained = new Dictionary<string, object?>();
            foreach (var name in new[] { "id", "name", "status", "error", "connectionStatus", "avgLatencyInMs", "minLatencyInMs", "maxLatencyInMs", "probesSent", "probesFailed", "hops" })
                if (root.TryGetProperty(name, out var value)) retained[name] = value.GetRawText().Length <= 4000 ? value.Clone() : "Details exceed the display limit.";
            if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("provisioningState", out var state) && state.ValueKind == JsonValueKind.String)
                retained["properties"] = new { provisioningState = state.GetString() };
            return JsonSerializer.SerializeToElement(retained);
        }
        catch (JsonException) { return null; }
    }
}