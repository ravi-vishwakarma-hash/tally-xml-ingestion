using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using TallyIngestion.Api;
using TallyIngestion.Application;
using TallyIngestion.Infrastructure;
using TallyIngestion.Models;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Keep the upload limit configurable so deployments can control request memory and processing cost.
var maxUploadBytes = builder.Configuration.GetValue<long>("MaxUploadBytes", 10 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxUploadBytes + 1024 * 1024);
var uploadPermitLimit = builder.Configuration.GetValue<int>("RateLimiting:Upload:PermitLimit", 10);
var uploadWindowSeconds = builder.Configuration.GetValue<int>("RateLimiting:Upload:WindowSeconds", 60);
var readPermitLimit = builder.Configuration.GetValue<int>("RateLimiting:Read:PermitLimit", 60);
var readWindowSeconds = builder.Configuration.GetValue<int>("RateLimiting:Read:WindowSeconds", 60);
if (uploadPermitLimit < 1 || uploadWindowSeconds < 1 || readPermitLimit < 1 || readWindowSeconds < 1)
{
    throw new InvalidOperationException("Rate-limiting permit limits and window durations must be positive values.");
}

// Register ASP.NET Core's RFC 7807 response service for consistent application/problem+json output.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddRateLimiter(options =>
{
    // Key upload permits by remote IP and reject immediately instead of queueing more memory-heavy requests.
    options.AddPolicy("tally-imports", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = uploadPermitLimit,
            Window = TimeSpan.FromSeconds(uploadWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        }));

    // Read routes get their own larger budget so an upload cannot consume their permits.
    options.AddPolicy("tally-reads", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = readPermitLimit,
            Window = TimeSpan.FromSeconds(readWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        }));

    options.OnRejected = async (rejection, cancellationToken) =>
    {
        var httpContext = rejection.HttpContext;

        // Surface the fixed-window reset time so clients can avoid retrying every request immediately.
        if (rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            httpContext.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        }

        await ApiResponseHandler.Problem(
            StatusCodes.Status429TooManyRequests,
            "rate_limit_exceeded",
            "Too many requests. Wait before retrying.",
            httpContext.Request.Path,
            httpContext.TraceIdentifier)
            .ExecuteAsync(httpContext);
    };
});
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Tally XML Ingestion API";
    document.Info.Version = "v1";
    document.Info.Description = "Upload Tally voucher exports and operation responses, then inspect import details and normalized vouchers.";
    return Task.CompletedTask;
}));

// A connection string opts into durable SQL Server storage; without one, local development uses memory.
var connectionString = builder.Configuration.GetConnectionString("Tally");
var storageProvider = builder.Configuration["Storage:Provider"]
    ?? (string.IsNullOrWhiteSpace(connectionString) ? "InMemory" : "SqlServer");
if (storageProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Configure ConnectionStrings:Tally when Storage:Provider is SqlServer.");
}

// Register one shared store instance for startup initialization and every request.
builder.Services.AddSingleton(new ImportStore(storageProvider, connectionString));
builder.Services.AddSingleton<IImportStore>(services => services.GetRequiredService<ImportStore>());
builder.Services.AddSingleton<XmlImporter>();
builder.Services.AddSingleton(new UploadOptions(maxUploadBytes));

var app = builder.Build();

// Add HSTS in production and redirect local HTTP requests before reaching API endpoints.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();

// Catch unhandled request exceptions before endpoint execution and serialize them with the shared ProblemDetails shape.
app.UseExceptionHandler();
app.UseRouting();
app.UseRateLimiter();

// Generate the OpenAPI v1 document and let Scalar render its interactive API reference.
app.MapOpenApi();
app.MapScalarApiReference("/docs", options => options.WithTitle("Tally XML Ingestion API"));

// SQL Server creates its tables before requests are accepted; memory storage needs no setup.
await app.Services.GetRequiredService<IImportStore>().InitializeAsync();
app.MapTallyEndpoints();
app.Run();
