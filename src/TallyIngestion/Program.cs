using Microsoft.AspNetCore.Http.Features;
using TallyIngestion.Api;
using TallyIngestion.Application;
using TallyIngestion.Infrastructure;
using TallyIngestion.Models;

var builder = WebApplication.CreateBuilder(args);

// Keep the upload limit configurable so deployments can control request memory and processing cost.
var maxUploadBytes = builder.Configuration.GetValue<long>("MaxUploadBytes", 10 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxUploadBytes + 1024 * 1024);

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

// SQL Server creates its tables before requests are accepted; memory storage needs no setup.
await app.Services.GetRequiredService<IImportStore>().InitializeAsync();
app.MapTallyEndpoints();
app.Run();