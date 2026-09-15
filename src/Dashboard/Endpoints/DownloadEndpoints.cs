using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.Endpoints;

/// <summary>
/// Single-use file downloads for generated HTML decks and script artifacts.
/// Requires an authenticated session; artifacts are additionally bound to the
/// user that generated them (decks/scripts contain tenant cost data, and
/// fileIds leak into logs and telemetry — the id alone must not be enough).
/// </summary>
public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        IResult Download(HttpContext ctx, string fileId, bool? inline)
        {
            var userId = ResolveUserId(ctx);
            if (userId is null) return Results.Unauthorized();
            var entry = ArtifactStore.Default.Find(fileId, userId.Value);
            if (entry is null)
                return Results.NotFound(new { error = "File not found or expired" });
            var bytes = File.ReadAllBytes(entry.Path);
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (entry.ContentType == "text/html")
                ctx.Response.Headers.ContentSecurityPolicy = "sandbox allow-scripts allow-downloads; default-src 'none'; script-src 'unsafe-inline' https://cdn.jsdelivr.net; style-src 'unsafe-inline' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; img-src data: https:; connect-src 'none'; form-action 'none'; base-uri 'none'";
            return inline == true && entry.ContentType == "text/html"
                ? Results.File(bytes, "text/html; charset=utf-8")
                : Results.File(bytes, entry.ContentType, entry.FileName);
        }
        app.MapGet("/api/download/html/{fileId}", Download);
        app.MapGet("/api/download/script/{fileId}", Download);
        app.MapGet("/api/download/file/{fileId}", Download);
    }

    /// <summary>Resolves the session user id (anonymous or Entra-derived). Null = no session.</summary>
    private static long? ResolveUserId(HttpContext ctx)
    {
        var userJson = ctx.Session.GetString("user");
        if (userJson is null) return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(userJson).GetProperty("id").GetInt64();
        }
        catch
        {
            return null;
        }
    }
}
