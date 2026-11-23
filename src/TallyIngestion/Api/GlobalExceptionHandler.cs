using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace TallyIngestion.Api;

/// <summary>Converts uncaught request exceptions into safe, traceable Problem Details responses.</summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Logs an unhandled server failure and writes a generic response without exposing exception details.</summary>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // An aborted request cannot receive a response, and a started response cannot safely be replaced.
        if (httpContext.RequestAborted.IsCancellationRequested || httpContext.Response.HasStarted)
        {
            return false;
        }

        logger.LogError(
            exception,
            "Unhandled exception for {Method} {Path}; trace ID {TraceId}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            httpContext.TraceIdentifier);

        httpContext.Response.Clear();
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        // The exception message and stack trace stay in server logs, never in the client response.
        var problemDetails = ApiResponseHandler.CreateProblemDetails(
            StatusCodes.Status500InternalServerError,
            "internal_server_error",
            "An unexpected error occurred while processing the request.",
            httpContext.Request.Path,
            httpContext.TraceIdentifier);

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });
    }
}
