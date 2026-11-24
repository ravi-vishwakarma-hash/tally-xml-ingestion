using TallyIngestion.Api;
using Xunit;

/// <summary>Checks the shared error response body contract used by endpoints and global exception handling.</summary>
public sealed class ApiResponseHandlerTests
{
    [Fact]
    public void ProblemDetailsIncludeHttpFieldsAndApplicationCode()
    {
        // Use a not-found response to verify both RFC Problem Details fields and application extensions.
        var problem = ApiResponseHandler.CreateProblemDetails(
            404,
            "import_not_found",
            "The requested import was not found.",
            "/api/tally/imports/missing",
            "trace-123");

        // Keep status, human-readable details, request instance, code, and trace correlation available to clients.
        Assert.Equal("about:blank", problem.Type);
        Assert.Equal("Not Found", problem.Title);
        Assert.Equal(404, problem.Status);
        Assert.Equal("The requested import was not found.", problem.Detail);
        Assert.Equal("/api/tally/imports/missing", problem.Instance);
        Assert.Equal("import_not_found", problem.Extensions["code"]);
        Assert.Equal("trace-123", problem.Extensions["traceId"]);
    }

    [Fact]
    public void RateLimitProblemUsesTooManyRequestsStatus()
    {
        // The upload limiter shares the same Problem Details builder and stable error-code extension.
        var problem = ApiResponseHandler.CreateProblemDetails(
            429,
            "rate_limit_exceeded",
            "Too many requests. Wait before retrying.",
            "/api/tally/imports",
            "trace-429");

        Assert.Equal("Too Many Requests", problem.Title);
        Assert.Equal(429, problem.Status);
        Assert.Equal("rate_limit_exceeded", problem.Extensions["code"]);
    }
}
