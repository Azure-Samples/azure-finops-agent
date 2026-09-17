using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dashboard.Tests;

public sealed class HttpHelperTests
{
    [Theory]
    [InlineData(false, 1, "query")]
    [InlineData(true, 1, "query")]
    [InlineData(true, 600, "query")]
    [InlineData(false, 1, "forecast")]
    public async Task CostThrottleWaitsAndReportsOneAutomaticRetry(bool exhaustRetry, int firstRetrySeconds, string operation)
    {
        var requests = new ConcurrentQueue<long>();
        var notices = new ConcurrentQueue<HttpHelper.RetryNotice>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/providers/Microsoft.CostManagement/" + operation, async context =>
        {
            requests.Enqueue(Stopwatch.GetTimestamp());
            if (requests.Count == 1 || exhaustRetry)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers["x-ms-ratelimit-microsoft.costmanagement-tenant-retry-after"] = requests.Count == 1 ? firstRetrySeconds.ToString() : "600";
                await context.Response.WriteAsJsonAsync(new { error = new { code = "429", message = "Synthetic throttle" } });
            }
            else
                await context.Response.WriteAsJsonAsync(new { properties = new { rows = new[] { new object[] { 42.73, "USD" } } } });
        });
        await server.StartAsync();
        var sessionId = "synthetic-cost-" + Guid.NewGuid().ToString("N");
        var reporterKey = "101:" + sessionId;
        HttpHelper.RetryReporters[reporterKey] = notice => { notices.Enqueue(notice); return Task.CompletedTask; };
        try
        {
            using var context = new ToolExecutionContext(sessionId, 101, CancellationToken.None);
            var url = server.Urls.Single() + "/providers/Microsoft.CostManagement/" + operation;
            var response = await HttpHelper.SendWithRetryAsync(url, sessionId, null, "cost-test", HttpMethod.Post, "{}", includeTimestamp: true);
            var expectedRequests = firstRetrySeconds > 300 ? 1 : 2;
            Assert.Equal(expectedRequests, requests.Count);
            if (expectedRequests == 2)
            {
                var dispatched = requests.ToArray();
                Assert.True(Stopwatch.GetElapsedTime(dispatched[0], dispatched[1]) >= TimeSpan.FromSeconds(1));
            }
            Assert.Equal(expectedRequests == 2, notices.First().WillRetry);
            Assert.NotNull(notices.First().RetryAtUtc);
            Assert.Contains("_finops", response);
            if (exhaustRetry)
            {
                Assert.StartsWith("HTTP 429", response);
                Assert.False(notices.Last().WillRetry);
                Assert.Equal(600, notices.Last().WaitSeconds);
                var blocked = await HttpHelper.SendWithRetryAsync(url, sessionId, null, "cost-test", HttpMethod.Post, "{\"different\":true}");
                Assert.Contains("CostManagementCooldown", blocked);
                Assert.Equal(expectedRequests, requests.Count);
                Assert.False(notices.Last().WillRetry);
            }
            else
            {
                Assert.StartsWith("HTTP 200", response);
                Assert.Single(notices);
            }
        }
        finally
        {
            HttpHelper.RetryReporters.TryRemove(reporterKey, out _);
            await server.StopAsync();
        }
    }

    [Theory]
    [InlineData("LogAnalyticsToken", "loganalytics")]
    [InlineData("StorageToken", "storage")]
    [InlineData("AzureToken", "base")]
    public void MissingTokensRouteToTheirOwnConsentTier(string token, string tier)
    {
        var response = HttpHelper.TokenMissing(token, null, "test");
        Assert.Contains("/auth/microsoft?tier=" + tier, response);
        Assert.Single(HttpHelper.ConsentActions(response));
        Assert.Empty(HttpHelper.ConsentActions(response.Replace("HTTP 401 ", "HTTP 403 ")));
    }

    [Fact]
    public void ToolSuppliedConsentUrlsCannotEscapeHostRoutes()
    {
        var response = "HTTP 401 Unauthorized\n{\"error\":{\"code\":\"consent_required\"},\"authActions\":[{\"href\":\"https://example.test/steal\"}]}";
        Assert.Empty(HttpHelper.ConsentActions(response));
    }

    [Fact]
    public async Task LaterRequestWaitsForExistingCostCooldownBeforeDispatch()
    {
        var requests = new ConcurrentQueue<long>();
        var notices = new ConcurrentQueue<HttpHelper.RetryNotice>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/providers/Microsoft.CostManagement/query", async context =>
        {
            requests.Enqueue(Stopwatch.GetTimestamp());
            if (requests.Count == 1)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers.RetryAfter = "1";
                await context.Response.WriteAsJsonAsync(new { error = new { code = "429" } });
            }
            else await context.Response.WriteAsJsonAsync(new { value = 42 });
        });
        await server.StartAsync();
        var sessionId = "synthetic-later-" + Guid.NewGuid().ToString("N");
        var reporterKey = "101:" + sessionId;
        HttpHelper.RetryReporters[reporterKey] = notice => { notices.Enqueue(notice); return Task.CompletedTask; };
        try
        {
            using var context = new ToolExecutionContext(sessionId, 101, CancellationToken.None);
            var url = server.Urls.Single() + "/providers/Microsoft.CostManagement/query";
            Assert.StartsWith("HTTP 429", await HttpHelper.SendWithRetryAsync(url, sessionId, null, "cost-test", HttpMethod.Post, "{}", maxAttemptsOverride: 1));
            var response = await HttpHelper.SendWithRetryAsync(url, sessionId, null, "cost-test", HttpMethod.Post, "{}");
            Assert.StartsWith("HTTP 200", response);
            Assert.Equal(2, requests.Count);
            var dispatched = requests.ToArray();
            Assert.True(Stopwatch.GetElapsedTime(dispatched[0], dispatched[1]) >= TimeSpan.FromSeconds(1));
            Assert.Contains(notices, notice => notice.Attempt == 0 && notice.WillRetry == true);
        }
        finally
        {
            HttpHelper.RetryReporters.TryRemove(reporterKey, out _);
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task HostCancellationReachesHttpTransport()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/slow", async (HttpContext context) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { disconnected.TrySetResult(); }
        });
        await server.StartAsync();
        using var cancellation = new CancellationTokenSource();
        using var scope = new ToolExecutionContext("synthetic-session", 101, cancellation.Token);
        var pending = HttpHelper.SendWithRetryAsync(server.Urls.Single() + "/slow", "synthetic-test-only", null, "test");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync();
    }
}