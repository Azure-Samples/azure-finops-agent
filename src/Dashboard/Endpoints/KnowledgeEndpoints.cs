using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Services;

namespace AzureFinOps.Dashboard.Endpoints;

/// <summary>
/// REST API for managing a user's persistent organizational knowledge articles
/// (subscription mappings, cost-center ownership, SLA targets, analysis
/// instructions, etc.). All routes are scoped to the authenticated user and are
/// an <b>Entra-only</b> feature: anonymous users get an ephemeral id, so any
/// article they created would be orphaned — they are rejected here.
/// </summary>
public static class KnowledgeEndpoints
{
    public sealed record CreateRequest(string? Title, string? Category, string? Content);
    public sealed record UpdateRequest(string? Title, string? Category, string? Content, bool? Active);

    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        // List metadata for all of the user's articles (no full content).
        app.MapGet("/api/knowledge", (HttpContext ctx) =>
        {
            if (!TryResolveEntraUser(ctx, out var userId)) return Results.Unauthorized();

            var items = KnowledgeStore.ListForUser(userId)
                .OrderBy(a => a.Category, StringComparer.Ordinal)
                .ThenBy(a => a.Title, StringComparer.Ordinal)
                .Select(a => new
                {
                    a.Id,
                    a.Title,
                    a.Category,
                    a.Active,
                    a.UpdatedUtc,
                    charCount = a.Content.Length,
                });

            return Results.Ok(new
            {
                articles = items,
                categories = KnowledgeStore.Categories,
                limits = new
                {
                    maxArticles = KnowledgeStore.MaxArticlesPerUser,
                    maxArticleChars = KnowledgeStore.MaxArticleChars,
                    maxTotalChars = KnowledgeStore.MaxTotalChars,
                },
            });
        });

        // Full content of a single article.
        app.MapGet("/api/knowledge/{id}", (HttpContext ctx, string id) =>
        {
            if (!TryResolveEntraUser(ctx, out var userId)) return Results.Unauthorized();

            var a = KnowledgeStore.Get(userId, id);
            return a is null
                ? Results.NotFound()
                : Results.Ok(new { a.Id, a.Title, a.Category, a.Content, a.Active, a.CreatedUtc, a.UpdatedUtc });
        });

        // Create a new article.
        app.MapPost("/api/knowledge", async (HttpContext ctx) =>
        {
            if (!TryResolveEntraUser(ctx, out var userId)) return Results.Unauthorized();

            var req = await ReadJsonAsync<CreateRequest>(ctx);
            if (req is null) return Results.BadRequest(new { error = "Invalid JSON body." });

            try
            {
                var a = KnowledgeStore.Create(userId, req.Title ?? "", req.Category ?? "custom", req.Content ?? "");
                UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;
                return Results.Ok(new { a.Id, a.Title, a.Category, a.Active, a.UpdatedUtc, charCount = a.Content.Length });
            }
            catch (KnowledgeValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Update an existing article (any omitted field is left unchanged).
        app.MapPut("/api/knowledge/{id}", async (HttpContext ctx, string id) =>
        {
            if (!TryResolveEntraUser(ctx, out var userId)) return Results.Unauthorized();
            if (!KnowledgeStore.IsValidId(id)) return Results.BadRequest(new { error = "Invalid id." });

            var req = await ReadJsonAsync<UpdateRequest>(ctx);
            if (req is null) return Results.BadRequest(new { error = "Invalid JSON body." });

            try
            {
                var a = KnowledgeStore.Update(userId, id, req.Title, req.Category, req.Content, req.Active);
                if (a is null) return Results.NotFound();
                UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;
                return Results.Ok(new { a.Id, a.Title, a.Category, a.Active, a.UpdatedUtc, charCount = a.Content.Length });
            }
            catch (KnowledgeValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Permanently delete an article.
        app.MapDelete("/api/knowledge/{id}", (HttpContext ctx, string id) =>
        {
            if (!TryResolveEntraUser(ctx, out var userId)) return Results.Unauthorized();

            var ok = KnowledgeStore.Delete(userId, id);
            return ok ? Results.Ok(new { deleted = true }) : Results.NotFound();
        });
    }

    /// <summary>
    /// Resolves the user id from the session and requires a stable Entra identity.
    /// Returns false (caller responds 401) for anonymous sessions — knowledge is
    /// Entra-only so ephemeral ids can't strand org data on disk. The user id is
    /// always derived server-side; it is never read from the request.
    /// </summary>
    private static bool TryResolveEntraUser(HttpContext ctx, out long userId)
    {
        userId = 0;

        var userJson = ctx.Session.GetString("user");
        if (userJson is null) return false;
        try
        {
            var user = JsonSerializer.Deserialize<JsonElement>(userJson);
            userId = user.GetProperty("id").GetInt64();
        }
        catch { return false; }

        var azureUserJson = ctx.Session.GetString("azure_user");
        if (azureUserJson is null) return false;
        try
        {
            var au = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
            if (!au.TryGetProperty("objectId", out var oidProp)) return false;
            var oid = oidProp.GetString();
            return !string.IsNullOrEmpty(oid);
        }
        catch { return false; }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx)
    {
        try { return await ctx.Request.ReadFromJsonAsync<T>(); }
        catch { return default; }
    }
}
