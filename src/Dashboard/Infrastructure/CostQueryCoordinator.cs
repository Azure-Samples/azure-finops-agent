using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureFinOps.Dashboard.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AzureFinOps.Dashboard.Infrastructure;

internal sealed class CostQueryCoordinator(TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, TenantState> _tenants = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 8 * 1024 * 1024 });
    private static readonly TimeSpan FreshLifetime = TimeSpan.FromMinutes(2);

    private sealed class TenantState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly object Sync = new();
        internal DateTimeOffset RetryAtUtc;
    }

    private sealed record CachedResponse(string Response, DateTimeOffset RetrievedUtc);

    internal async Task<string> ExecuteAsync(string tenantKey, string requestKey,
        Func<CancellationToken, Task<string>> send, CancellationToken cancellationToken)
    {
        var tenant = _tenants.GetOrAdd(tenantKey, _ => new TenantState());
        await tenant.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            DateTimeOffset retryAt;
            lock (tenant.Sync) retryAt = tenant.RetryAtUtc;
            var turn = CurrentTurn();
            var blocked = retryAt > now || turn?.CostQueriesBlocked == true;
            if (_cache.TryGetValue<CachedResponse>(requestKey, out var cached) && cached is not null
                && (blocked || now - cached.RetrievedUtc <= FreshLifetime))
                return Annotate(cached.Response, blocked ? "stale_during_cooldown" : "cached", cached.RetrievedUtc, retryAt);
            if (blocked)
                return "HTTP 429 TooManyRequests\n" + JsonSerializer.Serialize(new
                {
                    error = new { code = "CostManagementCooldown", message = "Cost Management is cooling down. No request was sent. Do not query this service again in this turn." },
                    retryAtUtc = retryAt,
                    blockedForTurn = turn?.CostQueriesBlocked == true
                });

            var response = await send(cancellationToken);
            if (response.StartsWith("HTTP 200 ", StringComparison.Ordinal))
            {
                if (response.Length <= 262144)
                    _cache.Set(requestKey, new CachedResponse(response, now), new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                        Size = response.Length * 2L + requestKey.Length * 2L
                    });
                return Annotate(response, "queried", now, default);
            }
            lock (tenant.Sync) retryAt = tenant.RetryAtUtc;
            return Annotate(response, "not_available", now, retryAt);
        }
        finally { tenant.Gate.Release(); }
    }

    internal void RecordThrottle(string tenantKey, double seconds)
    {
        var tenant = _tenants.GetOrAdd(tenantKey, _ => new TenantState());
        var now = _clock.GetUtcNow();
        var safeSeconds = double.IsFinite(seconds) && seconds > 0 ? seconds : 60;
        var deadline = safeSeconds >= (DateTimeOffset.MaxValue - now).TotalSeconds
            ? DateTimeOffset.MaxValue : now.AddSeconds(safeSeconds);
        lock (tenant.Sync)
            if (deadline > tenant.RetryAtUtc) tenant.RetryAtUtc = deadline;
        var turn = CurrentTurn();
        turn?.BlockCostQueries();
        if (turn is not null) turn.NextEligibleCostQueryUtc = deadline;
    }

    private static TurnExecution? CurrentTurn() =>
        ToolExecutionContext.Current?.SessionId is { } sessionId && TurnExecution.Active.TryGetValue(sessionId, out var turn) ? turn : null;

    internal static string TenantKey(string token)
    {
        try
        {
            var jwt = new JsonWebToken(token);
            if (jwt.TryGetClaim("tid", out var tenant)) return Digest("tenant:" + tenant.Value);
        }
        catch (ArgumentException) { }
        return Digest("credential:" + token);
    }

    internal static string RequestKey(string token, string method, string url, string? body)
    {
        var normalized = body ?? "";
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, document.RootElement);
                normalized = Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (JsonException) { }
        }
        return Digest(token + "\n" + method + "\n" + url + "\n" + normalized);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Annotate(string response, string cacheStatus, DateTimeOffset retrievedUtc, DateTimeOffset retryAt)
    {
        var separator = response.IndexOf('\n');
        if (separator < 0) return response;
        try
        {
            if (JsonNode.Parse(response[(separator + 1)..]) is not JsonObject body) return response;
            body["_finops"] = JsonSerializer.SerializeToNode(new
            {
                cacheStatus,
                retrievedAtUtc = retrievedUtc,
                dataAsOfUtc = (DateTimeOffset?)null,
                freshness = "Billing data may be delayed; retrieval time is not the billing data timestamp.",
                retryAtUtc = retryAt == default ? (DateTimeOffset?)null : retryAt
            });
            return response[..(separator + 1)] + body.ToJsonString();
        }
        catch (JsonException) { return response; }
    }

    public void Dispose()
    {
        _cache.Dispose();
        foreach (var state in _tenants.Values) state.Gate.Dispose();
    }
}