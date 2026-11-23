namespace TallyIngestion.Models;

/// <summary>Upload persistence result and API representation.</summary>
public sealed record SaveResult(object Body, bool Duplicate);
