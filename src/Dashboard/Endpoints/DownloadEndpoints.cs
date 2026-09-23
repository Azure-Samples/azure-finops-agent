using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

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
        app.MapGet("/api/download/html/{fileId}", (HttpContext ctx, string fileId, bool? inline) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId is null) return Results.Unauthorized();

            if (!HtmlPresentationTools.GeneratedFiles.TryGetValue(fileId, out var entry))
                return Results.NotFound(new { error = "File not found or expired" });

            // Owner mismatch returns the same 404 as a missing file — no oracle.
            if (entry.Owner is not null && entry.Owner != userId)
                return Results.NotFound(new { error = "File not found or expired" });

            if (!File.Exists(entry.Path))
            {
                HtmlPresentationTools.GeneratedFiles.TryRemove(fileId, out _);
                return Results.NotFound(new { error = "File no longer available" });
            }

            var fileName = Path.GetFileName(entry.Path);
            var downloadName = fileName.Contains('_') ? fileName[(fileName.IndexOf('_') + 1)..] : fileName;
            var bytes = File.ReadAllBytes(entry.Path);

            // ?inline=true serves the file in-browser (for the iframe preview / fullscreen view)
            // without ?inline=true the browser downloads the .html as a file.
            return inline == true
                ? Results.File(bytes, "text/html; charset=utf-8")
                : Results.File(bytes, "text/html; charset=utf-8", downloadName);
        });

        app.MapGet("/api/download/script/{fileId}", (HttpContext ctx, string fileId) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId is null) return Results.Unauthorized();

            if (!ScriptTools.GeneratedFiles.TryGetValue(fileId, out var entry))
                return Results.NotFound(new { error = "File not found or expired" });

            // Owner mismatch returns the same 404 as a missing file — no oracle.
            if (entry.Owner is not null && entry.Owner != userId)
                return Results.NotFound(new { error = "File not found or expired" });

            if (!File.Exists(entry.Path))
            {
                ScriptTools.GeneratedFiles.TryRemove(fileId, out _);
                return Results.NotFound(new { error = "File no longer available" });
            }

            var fileName = Path.GetFileName(entry.Path);
            var downloadName = fileName.Contains('_') ? fileName[(fileName.IndexOf('_') + 1)..] : fileName;
            var bytes = File.ReadAllBytes(entry.Path);
            var contentType = downloadName.EndsWith(".ps1") ? "application/x-powershell" : "application/x-shellscript";

            return Results.File(bytes, contentType, downloadName);
        });
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
