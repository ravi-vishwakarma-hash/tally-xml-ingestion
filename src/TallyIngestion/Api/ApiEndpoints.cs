using System.Security.Cryptography;
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
        endpoints.MapGet("/api/tally/imports/{id}", async (string id, IImportStore store, CancellationToken cancellationToken) =>
            await store.GetDetailsAsync(id, cancellationToken) is { } details
                ? Results.Json(details)
                : ApiError(404, "import_not_found", "The requested import was not found."));

        endpoints.MapGet("/api/tally/imports/{id}/vouchers", async (string id, IImportStore store, CancellationToken cancellationToken) =>
            await store.GetVouchersAsync(id, cancellationToken) is { } vouchers
                ? Results.Json(vouchers)
                : ApiError(404, "import_not_found", "The requested import was not found."));

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
            return ApiError(400, "invalid_content_type", "Expected multipart/form-data with a file field.");
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return ApiError(413, "upload_too_large", "Upload exceeds the configured size limit.");
        }

        // The assignment contract requires the multipart field to be named exactly "file".
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return ApiError(400, "file_required", "A non-empty multipart file field named 'file' is required.");
        }

        if (file.Length > options.MaxBytes)
        {
            return ApiError(413, "upload_too_large", "Upload exceeds the configured size limit.");
        }

        // Hash and parse the exact same byte sequence so identity never depends on filenames or XML formatting.
        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await input.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > options.MaxBytes)
        {
            return ApiError(413, "upload_too_large", "Upload exceeds the configured size limit.");
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
            return ApiError(400, "invalid_xml", exception.Message);
        }
    }

    /// <summary>Builds the common structured API error response.</summary>
    private static IResult ApiError(int statusCode, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: statusCode);
}
