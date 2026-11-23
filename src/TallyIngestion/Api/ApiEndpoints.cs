using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using TallyIngestion.Application;
using TallyIngestion.Models;

namespace TallyIngestion.Api;

/// <summary>Maps the HTTP routes that accept imports and return import data.</summary>
public static class ApiEndpoints
{
    /// <summary>Registers health, upload, import-detail, and normalized-voucher routes.</summary>
    public static IEndpointRouteBuilder MapTallyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The health route intentionally avoids touching storage so it can serve as a process liveness check.
        endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        endpoints.MapPost("/api/tally/imports", UploadAsync);

        // Missing imports return the same structured error shape across both read routes.
        endpoints.MapGet("/api/tally/imports/{id}", async (string id, IImportStore store, HttpContext context, CancellationToken cancellationToken) =>
            await store.GetDetailsAsync(id, cancellationToken) is { } details
                ? Results.Json(details)
                : ApiError(404, "import_not_found", "The requested import was not found.", context.Request.Path));

        endpoints.MapGet("/api/tally/imports/{id}/vouchers", async (string id, IImportStore store, HttpContext context, CancellationToken cancellationToken) =>
            await store.GetVouchersAsync(id, cancellationToken) is { } vouchers
                ? Results.Json(vouchers)
                : ApiError(404, "import_not_found", "The requested import was not found.", context.Request.Path));

        return endpoints;
    }

    /// <summary>Validates, parses, hashes, and persists one multipart upload.</summary>
    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        UploadOptions options,
        XmlImporter importer,
        IImportStore store,
        CancellationToken cancellationToken)
    {
        // Reject the wrong media type before asking ASP.NET to parse a multipart body.
        if (!request.HasFormContentType)
        {
            return ApiError(400, "invalid_content_type", "Expected multipart/form-data with a file field.", request.Path);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return ApiError(413, "upload_too_large", "Upload exceeds the configured size limit.", request.Path);
        }

        // The assignment contract requires the multipart field to be named exactly "file".
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return ApiError(400, "file_required", "A non-empty multipart file field named 'file' is required.", request.Path);
        }

        if (file.Length > options.MaxBytes)
        {
            return ApiError(413, "upload_too_large", "Upload exceeds the configured size limit.", request.Path);
        }

        // Hash and parse the exact same byte sequence so identity never depends on filenames or XML formatting.
        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await input.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > options.MaxBytes)
        {
            return ApiError(413, "upload_too_large", "Upload exceeds the configured size limit.", request.Path);
        }

        var bytes = buffer.ToArray();
        try
        {
            var parsed = importer.Parse(bytes);
            var contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var result = await store.SaveAsync(parsed, bytes, contentHash, cancellationToken);

            // Existing bytes are a successful idempotent lookup; only a new import receives 201 Created.
            return Results.Json(result.Body, statusCode: result.Duplicate ? 200 : 201);
        }
        catch (InvalidDataException exception)
        {
            // Parser details are intentionally replaced by a safe message rather than leaking stack traces or paths.
            return ApiError(400, "invalid_xml", exception.Message, request.Path);
        }
    }

    /// <summary>Builds the common structured API error response.</summary>
    private static IResult ApiError(int statusCode, string code, string message, PathString instance)
    {
        // RFC 7807 fields describe the HTTP failure; the extension preserves the machine-readable error code.
        var problem = new ProblemDetails
        {
            Type = "about:blank",
            Title = statusCode switch
            {
                400 => "Bad Request",
                404 => "Not Found",
                413 => "Payload Too Large",
                _ => "Request Failed"
            },
            Status = statusCode,
            Detail = message,
            Instance = instance
        };
        problem.Extensions["code"] = code;

        // The framework writes this as application/problem+json and applies standard ProblemDetails handling.
        return Results.Problem(problem);
    }
}
