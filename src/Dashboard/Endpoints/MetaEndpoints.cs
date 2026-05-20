using System.Diagnostics;

namespace AzureFinOps.Dashboard.Endpoints;

/// <summary>
/// Frontend-facing metadata endpoints (build version, App Insights config, model list).
/// </summary>
public static class MetaEndpoints
{
    public static void MapMetaEndpoints(
        this IEndpointRouteBuilder app,
        string appInsightsConnectionString,
        string azureOpenAIDeployment)
    {
        var (sha, build, branch) = ResolveBuildInfo();

        app.MapGet("/api/version", () => Results.Ok(new { sha, build, branch, started = DateTime.UtcNow.ToString("o") }));

        app.MapGet("/api/config", () => Results.Ok(new { appInsightsConnectionString = appInsightsConnectionString ?? "" }));

        app.MapGet("/api/models", (HttpContext ctx) =>
        {
            if (ctx.Session.GetString("user") is null)
                return Results.Unauthorized();

            return Results.Json(new[]
            {
                new { id = azureOpenAIDeployment, name = azureOpenAIDeployment }
            });
        });
    }

    private static (string Sha, string Build, string Branch) ResolveBuildInfo()
    {
        var sha = Environment.GetEnvironmentVariable("BUILD_SHA");
        if (string.IsNullOrEmpty(sha))
        {
            try { sha = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD") { RedirectStandardOutput = true, UseShellExecute = false })!.StandardOutput.ReadToEnd().Trim(); }
            catch { sha = "dev"; }
        }

        var build = Environment.GetEnvironmentVariable("BUILD_NUMBER");
        if (string.IsNullOrEmpty(build))
        {
            try { build = Process.Start(new ProcessStartInfo("git", "rev-list --count HEAD") { RedirectStandardOutput = true, UseShellExecute = false })!.StandardOutput.ReadToEnd().Trim(); }
            catch { build = "0"; }
        }

        var branch = Environment.GetEnvironmentVariable("BUILD_BRANCH");
        if (string.IsNullOrEmpty(branch))
        {
            try { branch = Process.Start(new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD") { RedirectStandardOutput = true, UseShellExecute = false })!.StandardOutput.ReadToEnd().Trim(); }
            catch { branch = "main"; }
        }

        return (sha!, build!, branch!);
    }
}
