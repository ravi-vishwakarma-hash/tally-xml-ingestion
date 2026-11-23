using Microsoft.AspNetCore.Http.Features;
using TallyIngestion.Api;
using TallyIngestion.Application;
using TallyIngestion.Infrastructure;
using TallyIngestion.Models;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Keep the upload limit configurable so deployments can control request memory and processing cost.
var maxUploadBytes = builder.Configuration.GetValue<long>("MaxUploadBytes", 10 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxUploadBytes + 1024 * 1024);
// Register ASP.NET Core's RFC 7807 response service for consistent application/problem+json output.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
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

// Catch unhandled request exceptions before endpoint execution and serialize them with the shared ProblemDetails shape.
app.UseExceptionHandler();

// Generate the OpenAPI v1 document and let Scalar render its interactive API reference.
app.MapOpenApi();
app.MapScalarApiReference("/docs", options => options.WithTitle("Tally XML Ingestion API"));

// SQL Server creates its tables before requests are accepted; memory storage needs no setup.
await app.Services.GetRequiredService<IImportStore>().InitializeAsync();
app.MapTallyEndpoints();
app.Run();
