using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dashboard.Tests;

public sealed class HttpHelperTests
{
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