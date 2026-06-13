using Microsoft.AspNetCore.Http;

namespace SolarPortal.Application.Interfaces.Services;

public interface IFileUploadService
{
    Task<(bool Success, string? FilePath, string? Error)> UploadAsync(IFormFile file, string subfolder);
    void DeleteFile(string filePath);

    /// <summary>
    /// Converts a stored relative URL (e.g. "/uploads/SCR-001/payments/abc.jpg")
    /// into an absolute URL using the current request's scheme + host
    /// (e.g. "https://localhost:7050/uploads/SCR-001/payments/abc.jpg").
    ///
    /// Used when storing references that need to be resolvable from OUTSIDE
    /// this app — e.g. the legacy SolFit DB's TrnProductorderDetail.ImageUpload
    /// column, which the legacy VB app reads. Without scheme + host the legacy
    /// app has no way to fetch the image.
    ///
    /// Returns the input unchanged if it's already absolute (starts with
    /// http:// or https://). Returns null if no HttpContext is available
    /// (background job, worker process) — caller should fall back to the
    /// relative path in that case.
    /// </summary>
    string? BuildAbsoluteUrl(string? relativeOrAbsoluteUrl);
}