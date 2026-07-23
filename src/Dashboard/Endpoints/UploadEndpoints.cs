using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace AzureFinOps.Dashboard.Endpoints;

/// <summary>
/// File-attachment endpoints. Lets users drop CSV/TSV/JSON/TXT/XLSX/PDF/Parquet
/// into the chat without granting Azure consent. Each upload is stored to temp,
/// previewed via the Python helper, and exposed to the LLM via QueryUploadedFile.
/// </summary>
public static class UploadEndpoints
{
    private const long MaxBytes = 100L * 1024 * 1024;

    public static void MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/upload", async (HttpContext ctx, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("AzureFinOps.Upload");
            var userJson = ctx.Session.GetString("user");
            if (userJson is null) return Results.Unauthorized();
            var userId = JsonSerializer.Deserialize<JsonElement>(userJson).GetProperty("id").GetInt64();

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await ctx.Request.ReadFormAsync();
            if (form.Files.Count == 0)
                return Results.BadRequest(new { error = "no file in request" });

            var results = new List<object>();
            foreach (var file in form.Files)
            {
                if (file.Length <= 0)
                {
                    results.Add(new { ok = false, fileName = file.FileName, error = "empty file" });
                    continue;
                }
                if (file.Length > MaxBytes)
                {
                    results.Add(new { ok = false, fileName = file.FileName, error = $"exceeds {MaxBytes / 1024 / 1024} MB" });
                    continue;
                }

                try
                {
                    await using var stream = file.OpenReadStream();
                    var (entry, previewJson) = await UploadedFileTools.RegisterAsync(userId, stream, file.FileName, file.Length);
                    object preview;
                    try
                    {
                        using var previewDoc = JsonDocument.Parse(previewJson);
                        preview = previewDoc.RootElement.Clone();
                    }
                    catch (JsonException jex)
                    {
                        preview = new { ok = false, error = $"preview produced invalid JSON: {jex.Message}", raw = previewJson.Length > 500 ? previewJson[..500] + "..." : previewJson };
                    }
                    results.Add(new
                    {
                        ok = true,
                        fileId = entry.FileId,
                        fileName = entry.FileName,
                        kind = entry.Kind,
                        sizeBytes = entry.SizeBytes,
                        preview
                    });
                }
                catch (InvalidOperationException ex)
                {
                    results.Add(new { ok = false, fileName = file.FileName, error = ex.Message });
                }
                catch (Exception ex)
                {
                    // Unexpected upload-processing failure (expected validation
                    // errors are surfaced as InvalidOperationException above). Log it
                    // so upload reliability is trackable in Application Insights — the
                    // caller still gets the per-file error back in the response body.
                    logger.LogError(ex, "Upload processing failed for {FileName} ({Bytes} bytes)", file.FileName, file.Length);
                    results.Add(new { ok = false, fileName = file.FileName, error = $"{ex.GetType().Name}: {ex.Message}" });
                }
            }

            return Results.Ok(new { files = results });
        })
        .DisableAntiforgery()
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxBytes + 10L * 1024 * 1024)); // file cap + multipart overhead

        app.MapGet("/api/uploads", (HttpContext ctx) =>
        {
            var userJson = ctx.Session.GetString("user");
            if (userJson is null) return Results.Unauthorized();
            var userId = JsonSerializer.Deserialize<JsonElement>(userJson).GetProperty("id").GetInt64();
            var list = UploadedFileTools.ListForUser(userId)
                .Select(e => new { fileId = e.FileId, fileName = e.FileName, kind = e.Kind, sizeBytes = e.SizeBytes });
            return Results.Ok(new { files = list });
        });

        app.MapDelete("/api/uploads/{fileId}", (HttpContext ctx, string fileId) =>
        {
            var userJson = ctx.Session.GetString("user");
            if (userJson is null) return Results.Unauthorized();
            var userId = JsonSerializer.Deserialize<JsonElement>(userJson).GetProperty("id").GetInt64();
            return UploadedFileTools.RemoveForUser(userId, fileId)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }
}
