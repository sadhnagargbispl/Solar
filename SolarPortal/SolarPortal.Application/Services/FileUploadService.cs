using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Interfaces.Services;

namespace SolarPortal.Application.Services;

public class FileUploadService : IFileUploadService
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly IHttpContextAccessor _httpCtx;
    private readonly string[] _allowedExtensions = { ".jpg", ".jpeg", ".png", ".pdf" };
    private readonly long _maxFileSize = 10 * 1024 * 1024; // 10 MB

    public FileUploadService(
        IWebHostEnvironment env,
        IConfiguration config,
        IHttpContextAccessor httpCtx)
    {
        _env = env;
        _config = config;
        _httpCtx = httpCtx;
    }

    // Cleans a caller-supplied subfolder like "SCR-001/dcr" into a safe relative
    // path under /uploads. Each segment keeps only [A-Za-z0-9-_]; empty or unsafe
    // segments (".", "..") are dropped. Segments are re-joined with "/".
    private static string SanitizeSubfolder(string? subfolder)
    {
        if (string.IsNullOrWhiteSpace(subfolder)) return "misc";
        var segments = subfolder
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(seg =>
            {
                var clean = new string(seg.Where(c =>
                    char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                return clean;
            })
            .Where(seg => seg.Length > 0 && seg != "." && seg != "..")
            .ToArray();
        return segments.Length > 0 ? string.Join("/", segments) : "misc";
    }

    public async Task<(bool Success, string? FilePath, string? Error)> UploadAsync(
        IFormFile file, string subfolder)
    {
        if (file == null || file.Length == 0)
            return (false, null, "No file provided");

        if (file.Length > _maxFileSize)
            return (false, null, "File exceeds 10MB limit");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!_allowedExtensions.Contains(ext))
            return (false, null, "Invalid file type. Allowed: JPG, PNG, PDF");

        // Sanitise the subfolder. Callers pass things like "SCR-001/dcr" to
        // organise files per project. We allow letters, digits, dash, underscore
        // and the path separator between segments; everything else (spaces, "..",
        // drive letters, stray slashes) is stripped so we never escape /uploads.
        subfolder = SanitizeSubfolder(subfolder);

        var uploadFolder = Path.Combine(_env.WebRootPath, "uploads", subfolder);
        Directory.CreateDirectory(uploadFolder);

        var uniqueName = $"{Guid.NewGuid()}{ext}";
        var fullPath = Path.Combine(uploadFolder, uniqueName);

        using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // ─── URL that gets stored in DB ──────────────────────────────────
        // We store the RELATIVE URL ("/uploads/<subfolder>/<file>"). This is
        // what existing views + admin file mirroring already expect, so the
        // hundreds of existing rendered <img src="/uploads/..."> tags keep
        // working without changes.
        //
        // For callers that need to send an ABSOLUTE URL elsewhere (e.g. the
        // legacy SolFit bridge populating TrnProductorderDetail.ImageUpload —
        // legacy VB app can't resolve our relative paths), they call
        // BuildAbsoluteUrl(relativeUrl) at the point of use. That keeps
        // storage clean + portable while still giving the legacy DB a
        // self-contained URL.
        var relativeUrl = $"/uploads/{subfolder}/{uniqueName}";

        // ─── Mirror the file into the ADMIN panel's wwwroot ──────────────────
        // The admin panel is a separate app with its own wwwroot. When the user
        // uploads a receipt / document here, the admin can't see it unless the
        // file also lives under admin's wwwroot/uploads. So we copy it there too,
        // keeping the SAME relative path ("/uploads/<subfolder>/<file>"). This
        // means a plain <img src="/uploads/..."> works on BOTH panels — no
        // cross-panel static mapping or special controller needed.
        //
        // The admin wwwroot is found via:
        //   1. config "AdminUploadsMirrorPath"  (explicit wwwroot/uploads path)
        //   2. auto-probing common sibling folder layouts
        // If none is found, we silently skip the mirror — the user-side file is
        // still saved, and the admin FileController fallback can still resolve it.
        try
        {
            var adminUploadsRoot = ResolveAdminUploadsRoot();
            if (!string.IsNullOrWhiteSpace(adminUploadsRoot))
            {
                var adminFolder = Path.Combine(adminUploadsRoot, subfolder);
                Directory.CreateDirectory(adminFolder);
                var adminFullPath = Path.Combine(adminFolder, uniqueName);
                File.Copy(fullPath, adminFullPath, overwrite: true);
            }
        }
        catch
        {
            // Non-fatal: the user-side save succeeded. Admin display can still
            // fall back to the FileController that probes the user wwwroot.
        }

        return (true, relativeUrl, null);
    }

    /// <summary>
    /// Builds an absolute URL ({scheme}://{host}{relativeUrl}) from the
    /// current HTTP request. Returns null if there's no HttpContext (e.g.
    /// the upload was triggered from a background job, a worker process or
    /// a unit test) — callers should fall back to the relative URL.
    ///
    /// Why we expose this:
    ///   • The legacy SolFit DB (TrnProductorderDetail.ImageUpload column) is
    ///     read by the legacy VB app on a different port — relative URLs
    ///     don't resolve there. We pass the absolute URL so legacy can
    ///     &lt;img src="..."&gt; directly from the saved value.
    ///   • Saved DB value is self-contained — paste into any browser tab and
    ///     it loads, no need to know which app served it.
    ///   • Exports / shared links work without having to know the host.
    ///
    /// We trust X-Forwarded-Proto / X-Forwarded-Host (handled upstream by
    /// HttpContext.Request) so a reverse-proxy deployment gets the public
    /// scheme + host, not the internal one.
    ///
    /// Inputs already absolute (http:// or https://) are returned unchanged
    /// so this is idempotent — safe to call on a value that may already be
    /// in either form.
    /// </summary>
    public string? BuildAbsoluteUrl(string? relativeOrAbsoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsoluteUrl)) return null;

        var url = relativeOrAbsoluteUrl.Trim();

        // Already absolute — idempotent return.
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var req = _httpCtx.HttpContext?.Request;
        if (req == null) return null;
        if (string.IsNullOrEmpty(req.Scheme) || !req.Host.HasValue) return null;

        // Ensure the relative part has a leading slash so concatenation
        // produces a valid URL ("http://host" + "/path" = "http://host/path").
        var relative = url.StartsWith("/") ? url : "/" + url;
        // Request.Host already includes the port when present, e.g. "localhost:7050"
        return $"{req.Scheme}://{req.Host.Value}{relative}";
    }

    // Locate the admin panel's wwwroot/uploads folder so uploaded files can be
    // mirrored there. Returns null if it can't be found.
    private string? ResolveAdminUploadsRoot()
    {
        // 1. Explicit config wins.
        var configured = _config["AdminUploadsMirrorPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        // 2. Probe common layouts relative to THIS (user) app's content root.
        //    Typical side-by-side dev layout:
        //      <root>/UserPanel/SolarPortal/SolarPortal/SolarPortal.Web        (this app)
        //      <root>/AdminPanel/Soller_Admin/Soller_Admin/SolarPortal/SolarPortal.AdminWeb/wwwroot/uploads
        var contentRoot = _env.ContentRootPath;
        string[] candidates =
        {
            Path.Combine(contentRoot, "..", "..", "..", "..", "..",
                         "AdminPanel", "Soller_Admin", "Soller_Admin", "SolarPortal",
                         "SolarPortal.AdminWeb", "wwwroot", "uploads"),
            Path.Combine(contentRoot, "..", "..", "..", "..",
                         "Soller_Admin", "Soller_Admin", "SolarPortal",
                         "SolarPortal.AdminWeb", "wwwroot", "uploads"),
            Path.Combine(contentRoot, "..", "SolarPortal.AdminWeb", "wwwroot", "uploads"),
        };
        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                // The admin wwwroot itself must exist (its parent dir). We create
                // the uploads subfolder if needed, but only if the wwwroot is real
                // — we don't want to fabricate a wrong directory tree.
                var wwwroot = Directory.GetParent(full)?.FullName;
                if (wwwroot != null && Directory.Exists(wwwroot))
                    return full;
            }
            catch { /* ignore bad candidate */ }
        }
        return null;
    }

    public void DeleteFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        // ─── Normalise: accept both absolute URLs and relative paths ─────────
        // Storage is normally relative ("/uploads/..."), but in case a caller
        // ever passes back the absolute form (e.g. from a legacy column),
        // we tolerate it by stripping the scheme + host first.
        var localPath = filePath;
        if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri))
            localPath = uri.AbsolutePath;        // -> "/uploads/SCR-001/payments/<guid>.jpg"

        var fullPath = Path.Combine(_env.WebRootPath, localPath.TrimStart('/'));
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        // Also remove the mirrored copy from the admin wwwroot if present.
        try
        {
            var adminUploadsRoot = ResolveAdminUploadsRoot();
            if (!string.IsNullOrWhiteSpace(adminUploadsRoot))
            {
                // localPath is "/uploads/<subfolder>/<file>"; strip the leading
                // "/uploads/" to get "<subfolder>/<file>" relative to admin uploads.
                var rel = localPath.Replace("\\", "/").TrimStart('/');
                if (rel.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring("uploads/".Length);
                var adminFullPath = Path.Combine(adminUploadsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(adminFullPath))
                    File.Delete(adminFullPath);
            }
        }
        catch { /* non-fatal */ }
    }
}
