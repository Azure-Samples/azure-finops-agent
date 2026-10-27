namespace AzureFinOps.Dashboard.Endpoints;

internal static class UnmatchedEndpoints
{
    /// <summary>
    /// An unmatched API or auth GET is a 404 for the caller, not the single-page app's HTML with a 200. Mapped routes
    /// keep their priority over these catch-alls, and pages outside /api and /auth still fall back to the app.
    /// </summary>
    internal static void MapUnmatchedApiNotFound(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/{**path}", () => Results.NotFound(new { error = "Not found" }));
        app.MapGet("/auth/{**path}", () => Results.NotFound(new { error = "Not found" }));
    }
}
