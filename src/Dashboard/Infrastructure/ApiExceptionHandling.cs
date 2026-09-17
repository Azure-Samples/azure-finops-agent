using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace AzureFinOps.Dashboard.Infrastructure;

internal static class ApiExceptionHandling
{
    internal static ExceptionHandlerOptions CreateOptions(ILogger logger) => new()
    {
        SuppressDiagnosticsCallback = context => true,
        ExceptionHandler = async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            var aborted = context.RequestAborted.IsCancellationRequested || exception is OperationCanceledException;

            if (aborted)
            {
                logger.LogInformation("Request aborted on {Method} {Path} (client disconnect, traceId={TraceId})",
                    context.Request.Method, context.Request.Path.Value, traceId);
            }
            else if (exception is not null)
            {
                logger.LogError(exception, "Unhandled exception on {Method} {Path} (traceId={TraceId})",
                    context.Request.Method, context.Request.Path.Value, traceId);
                context.Features.Get<IHttpMetricsTagsFeature>()?.Tags.Add(
                    new KeyValuePair<string, object?>("error.type", exception.GetType().FullName));
            }

            if (!context.Response.HasStarted && !aborted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred.", traceId });
            }
        }
    };
}