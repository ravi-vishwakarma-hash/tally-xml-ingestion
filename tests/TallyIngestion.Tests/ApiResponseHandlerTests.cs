using TallyIngestion.Api;
using Xunit;

public sealed class ApiResponseHandlerTests
{
    [Fact]
    public void ProblemDetailsIncludeHttpFieldsAndApplicationCode()
    {
        var problem = ApiResponseHandler.CreateProblemDetails(
            404,
            "import_not_found",
            "The requested import was not found.",
            "/api/tally/imports/missing",
            "trace-123");

        Assert.Equal("about:blank", problem.Type);
        Assert.Equal("Not Found", problem.Title);
        Assert.Equal(404, problem.Status);
        Assert.Equal("The requested import was not found.", problem.Detail);
        Assert.Equal("/api/tally/imports/missing", problem.Instance);
        Assert.Equal("import_not_found", problem.Extensions["code"]);
        Assert.Equal("trace-123", problem.Extensions["traceId"]);
    }
}
