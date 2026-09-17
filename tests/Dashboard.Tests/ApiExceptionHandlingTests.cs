using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dashboard.Tests;

public sealed class ApiExceptionHandlingTests
{
    [Fact]
    public async Task GenuineFaultIsLoggedOnceWithCorrelationAndSafeResponse()
    {
        using var logs = new CapturingLoggerProvider();
        var metrics = new CapturingMetricsTags();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        await using var app = builder.Build();
        app.UseExceptionHandler(ApiExceptionHandling.CreateOptions(
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureFinOps.AI")));
        var exception = new InvalidOperationException("Synthetic internal failure");
        app.MapGet("/failure", (HttpContext context) =>
        {
            context.Features.Set<IHttpMetricsTagsFeature>(metrics);
            throw exception;
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/failure");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("An unexpected error occurred.", json.RootElement.GetProperty("error").GetString());
        var traceId = json.RootElement.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.DoesNotContain(exception.Message, body);
        var entry = Assert.Single(logs.Entries, entry => entry.Exception is not null);
        Assert.Same(exception, entry.Exception);
        Assert.Equal("AzureFinOps.AI", entry.Category);
        Assert.Contains("GET /failure", entry.Message);
        Assert.Contains(traceId!, entry.Message);
        Assert.Contains(metrics.Tags, tag => tag.Key == "error.type" && Equals(tag.Value, exception.GetType().FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationIsTraceOnlyWithoutAnErrorBody(bool requestAborted)
    {
        using var logs = new CapturingLoggerProvider();
        var metrics = new CapturingMetricsTags();
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Method = "GET";
        context.Request.Path = "/cancel";
        context.RequestAborted = new CancellationToken(requestAborted);
        context.Features.Set<IHttpMetricsTagsFeature>(metrics);
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
        {
            Error = requestAborted ? new IOException("Disconnected") : new OperationCanceledException()
        });
        var options = ApiExceptionHandling.CreateOptions(logs.CreateLogger("AzureFinOps.AI"));

        await options.ExceptionHandler!(context);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("Request aborted", entry.Message);
        Assert.Equal(0, body.Length);
        Assert.Empty(metrics.Tags);
    }

    [Fact]
    public async Task StartedResponseFaultRetainsFrameworkExceptionDiagnostics()
    {
        using var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        await using var app = builder.Build();
        app.UseExceptionHandler(ApiExceptionHandling.CreateOptions(
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureFinOps.AI")));
        var exception = new InvalidOperationException("Synthetic streaming failure");
        app.MapGet("/stream", async (HttpContext context) =>
        {
            await context.Response.WriteAsync("started");
            throw exception;
        });
        await app.StartAsync();
        using var client = app.GetTestClient();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var response = await client.GetAsync("/stream");
            await response.Content.ReadAsStringAsync();
        });

        var entry = Assert.Single(logs.Entries, entry => entry.Exception is not null);
        Assert.Same(exception, entry.Exception);
        Assert.Equal("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", entry.Category);
    }

    private sealed class CapturingMetricsTags : IHttpMetricsTagsFeature
    {
        public ICollection<KeyValuePair<string, object?>> Tags { get; } = new List<KeyValuePair<string, object?>>();
        public bool MetricsDisabled { get; set; }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new(category, logLevel, formatter(state, exception), exception));
        }
    }
}