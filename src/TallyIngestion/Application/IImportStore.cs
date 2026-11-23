using TallyIngestion.Models;

namespace TallyIngestion.Application;

/// <summary>Defines the persistence operations required by the import API.</summary>
public interface IImportStore
{
    /// <summary>Initializes resources used by the configured persistence provider.</summary>
    Task InitializeAsync();

    /// <summary>Saves parsed content atomically or returns the existing record for identical bytes.</summary>
    Task<SaveResult> SaveAsync(ParsedImport parsed, byte[] raw, string hash, CancellationToken cancellationToken);

    /// <summary>Returns safe metadata for an import, or null when its ID is unknown.</summary>
    Task<object?> GetDetailsAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns normalized vouchers for an import, or null when its ID is unknown.</summary>
    Task<object?> GetVouchersAsync(string id, CancellationToken cancellationToken);
}