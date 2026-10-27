using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.Endpoints;

/// <summary>
/// Frontend-facing metadata endpoints (build version, App Insights config, model list).
/// </summary>
public static class MetaEndpoints
{
    // Captured once at class init. This used to be DateTime.UtcNow evaluated per
    // request, so `started` just echoed the current time — it always looked like a
    // fresh restart, which made it useless for confirming a deploy had rolled over
    // (and produced a false "still broken" reading when a test ran against the old
    // container).
    private static readonly string StartedUtc = DateTime.UtcNow.ToString("o");

    public static void MapMetaEndpoints(
        this IEndpointRouteBuilder app,
        string appInsightsConnectionString,
        string azureOpenAIDeployment)
    {
        var (sha, build, branch) = (BuildInfo.Sha, BuildInfo.Number, BuildInfo.Branch);

        app.MapGet("/api/version", () => Results.Ok(new { sha, build, branch, started = StartedUtc }));

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
}
