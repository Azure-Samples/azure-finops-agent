using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.Feedback;

/// <summary>
/// The feedback form. <c>GET /api/feedback/config</c> tells the form whether it posts directly or hands the
/// user a prefilled GitHub page; <c>POST /api/feedback</c> validates the submission and either creates the
/// issue through the GitHub App or returns that page's link. Responses never carry GitHub's own errors.
/// </summary>
public static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(
        this IEndpointRouteBuilder app,
        GitHubAppClient github,
        FeedbackRateLimiter limiter,
        ILogger logger)
    {
        // Both handlers take a bound CancellationToken: a lambda whose only parameter is HttpContext binds to
        // the RequestDelegate overload, which silently discards the returned result.
        app.MapGet("/api/feedback/config", async (HttpContext ctx, CancellationToken cancellationToken) => Results.Ok(new
        {
            mode = await github.IsAvailableAsync(cancellationToken) ? "direct" : "link",
            repository = github.Repository,
        }));

        app.MapPost("/api/feedback", async (HttpContext ctx, CancellationToken cancellationToken) =>
        {
            if (!SessionIdentity.TryResolve(ctx, out var userId, out _, out _, out _)) return Results.Unauthorized();

            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { field = "form", error = "Send the feedback form's fields." });
            }
            if (!FeedbackReport.TryRead(body, out var request, out var error))
                return Results.Json(new { field = error!.Field, error = error.Message },
                    statusCode: error.Sensitive ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status400BadRequest);

            var issue = FeedbackReport.Compose(request, BuildInfo.Describe());
            var link = FeedbackReport.NewIssueUrl(github.Repository, issue);
            if (!await github.IsAvailableAsync(cancellationToken))
                return Results.Ok(new { mode = "link", url = link });

            if (!limiter.TryAcquire(userId, out var wait))
            {
                var seconds = (int)Math.Ceiling(wait.TotalSeconds);
                ctx.Response.Headers.RetryAfter = seconds.ToString();
                return Results.Json(new
                {
                    error = "You've sent a lot of feedback in a short time. Try again later, or post it on GitHub yourself.",
                    retryAfterSeconds = seconds,
                    fallbackUrl = link,
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            try
            {
                var created = await github.CreateIssueAsync(issue, cancellationToken);
                logger.LogInformation("Feedback issue #{Number} created ({Kind}).", created.Number, request.Kind);
                return Results.Json(new { mode = "direct", number = created.Number, url = created.Url }, statusCode: StatusCodes.Status201Created);
            }
            catch (GitHubFeedbackUnavailableException ex)
            {
                logger.LogWarning("Feedback fell back to GitHub's new-issue page: {Reason}", ex.Message);
                return Results.Ok(new { mode = "link", url = link });
            }
            catch (Exception ex) when (GitHubAppClient.IsGitHubFailure(ex, cancellationToken))
            {
                logger.LogWarning("Feedback issue creation failed: {Failure}", ex.GetType().Name);
                return Results.Json(new
                {
                    error = "GitHub didn't accept the feedback just now. Try again, or post it on GitHub yourself.",
                    fallbackUrl = link,
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
