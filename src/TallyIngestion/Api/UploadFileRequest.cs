using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace TallyIngestion.Api;

/// <summary>Describes the multipart upload shape used to generate the OpenAPI request schema.</summary>
public sealed class UploadFileRequest
{
    /// <summary>XML document provided in the required multipart field named <c>file</c>.</summary>
    [Required]
    [FromForm(Name = "file")]
    public IFormFile? File { get; init; }
}
