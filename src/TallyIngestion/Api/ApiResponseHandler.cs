using Microsoft.AspNetCore.Mvc;

namespace TallyIngestion.Api;

/// <summary>Creates consistent success and Problem Details responses for HTTP endpoints.</summary>
public static class ApiResponseHandler
{
    /// <summary>Returns a standard JSON success response.</summary>
    public static IResult Ok(object response) => Results.Ok(response);

    /// <summary>Returns a JSON response with an explicit status code, such as 201 or duplicate 200.</summary>
    public static IResult Json(object response, int statusCode) => Results.Json(response, statusCode: statusCode);

    /// <summary>Builds a standard Problem Details error response with a stable application error code.</summary>
    public static IResult Problem(int statusCode, string code, string detail, string? instance = null, string? traceId = null)
    {
        var problemDetails = CreateProblemDetails(statusCode, code, detail, instance, traceId);
        return Results.Problem(problemDetails);
    }

    /// <summary>Creates the shared Problem Details body used by both endpoint errors and exception handling.</summary>
    public static ProblemDetails CreateProblemDetails(
        int statusCode,
        string code,
        string detail,
        string? instance = null,
        string? traceId = null)
    {
        var problemDetails = new ProblemDetails
        {
            Type = "about:blank",
            Title = GetTitle(statusCode),
            Status = statusCode,
            Detail = detail,
            Instance = instance
        };

        // Extensions carry stable machine-readable diagnostics without changing standard Problem Details fields.
        problemDetails.Extensions["code"] = code;
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            problemDetails.Extensions["traceId"] = traceId;
        }

        return problemDetails;
    }

    private static string GetTitle(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        404 => "Not Found",
        413 => "Payload Too Large",
        500 => "Internal Server Error",
        _ => "Request Failed"
    };
}
