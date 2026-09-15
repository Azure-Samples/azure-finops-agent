using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Endpoints;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Tests;

public sealed class ArtifactTests
{
    [Theory]
    [InlineData("csv", "text/csv")]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("html", "text/html")]
    public async Task ReportToolCreatesRealOwnerBoundFiles(string format, string contentType)
    {
        var tool = new ReportTools(101).Create().Single();
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["format"] = format,
            ["dataJson"] = "{\"title\":\"Synthetic\",\"source\":\"Synthetic data; timestamp unknown\",\"sheets\":[{\"columns\":[\"Month\",\"Cost\"],\"rows\":[[\"Jan\",30],[\"Feb\",30]],\"sourceRowCount\":2}]}"
        });
        var marker = Assert.IsType<JsonElement>(result).GetString()!;
        Assert.StartsWith("__HTML_READY__:", marker);
        var identifier = marker.Split(':')[1];
        try
        {
            var entry = ArtifactStore.Default.Find(identifier, 101);
            Assert.NotNull(entry);
            Assert.Equal(contentType, entry.ContentType);
            Assert.True(new FileInfo(entry.Path).Length > 20);
            Assert.Null(ArtifactStore.Default.Find(identifier, 202));
        }
        finally { ArtifactStore.Default.Remove(identifier, 101); }
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(101L, -1)]
    public void PersistedOwnerlessOrExpiredArtifactsAreNeverServed(long? owner, int lifetimeHours)
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-artifact-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var identifier = Guid.NewGuid().ToString("N");
        try
        {
            var payload = Path.Combine(root, identifier + ".data");
            File.WriteAllText(payload, "synthetic");
            File.WriteAllText(Path.Combine(root, identifier + ".json"), JsonSerializer.Serialize(
                new ArtifactStore.Entry(identifier, owner, "synthetic.html", "text/html", DateTime.UtcNow.AddHours(lifetimeHours), payload)));
            var store = new ArtifactStore(root);
            Assert.Null(store.Find(identifier, 101));
            Assert.False(File.Exists(payload));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("script", null, 202L, HttpStatusCode.NotFound)]
    [InlineData("html", null, 202L, HttpStatusCode.NotFound)]
    [InlineData("script", 101L, 202L, HttpStatusCode.NotFound)]
    [InlineData("html", 101L, 202L, HttpStatusCode.NotFound)]
    [InlineData("script", 101L, 101L, HttpStatusCode.OK)]
    [InlineData("html", 101L, 101L, HttpStatusCode.OK)]
    [InlineData("script", 101L, null, HttpStatusCode.Unauthorized)]
    [InlineData("html", 101L, null, HttpStatusCode.Unauthorized)]
    public async Task DownloadsRequireMatchingOwner(string kind, long? owner, long? caller, HttpStatusCode expected)
    {
        var artifact = owner is null ? null : ArtifactStore.Default.Register(owner.Value, "test.html", "text/html", "synthetic report"u8.ToArray());
        var identifier = artifact?.Id ?? Guid.NewGuid().ToString("N");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSession();
            await using var app = builder.Build();
            app.UseSession();
            app.Use(async (context, next) =>
            {
                if (caller is not null)
                    context.Session.SetString("user", JsonSerializer.Serialize(new { id = caller.Value }));
                await next(context);
            });
            app.MapDownloadEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var response = await client.GetAsync($"/api/download/{kind}/{identifier}");
            Assert.Equal(expected, response.StatusCode);
        }
        finally
        {
            if (owner is not null) ArtifactStore.Default.Remove(identifier, owner.Value);
        }
    }

    [Theory]
    [InlineData("script")]
    [InlineData("deck")]
    [InlineData("assessment")]
    public async Task GenerationBindsOwnerWithoutTracingContext(string kind)
    {
        const long owner = 101;
        var previousActivity = Activity.Current;
        Activity.Current = null;
        try
        {
            var tool = kind switch
            {
                "script" => new ScriptTools(owner).Create().Single(),
                "deck" => new HtmlPresentationTools(owner).Create().Single(),
                _ => new MaturityReportTools(owner).Create().Single()
            };
            var arguments = new AIFunctionArguments
            {
                ["filename"] = "regression-report",
                ["customer"] = null,
                ["scriptContent"] = "Write-Output 'synthetic report'",
                ["language"] = "powershell",
                ["description"] = "Synthetic regression report",
                ["slidesJson"] = "[{\"layout\":\"title\",\"title\":\"Synthetic\"}]",
                ["reportJson"] = "{\"capabilities\":[]}"
            };
            var marker = (await tool.InvokeAsync(arguments))!.ToString()!;
            var identifier = marker.Split(':')[1];
            var entry = ArtifactStore.Default.Find(identifier, owner);
            try { Assert.NotNull(entry); Assert.Equal(owner, entry.Owner); }
            finally { ArtifactStore.Default.Remove(identifier, owner); }
        }
        finally { Activity.Current = previousActivity; }
    }
}