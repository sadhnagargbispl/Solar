using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Web.Areas.SolarPanelInstaller.Controllers;

[Area("SolarPanelInstaller")]
[Authorize(Roles = "Installer")]
public class ConnectionsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IFileUploadService _fileUpload;

    public ConnectionsController(ApplicationDbContext db, IFileUploadService fileUpload)
    {
        _db = db;
        _fileUpload = fileUpload;
    }

    private int WorkerId => int.TryParse(User.FindFirst("WorkerId")?.Value, out var id) ? id : 0;

    // Report — this installer's connections with status.
    public async Task<IActionResult> Index()
    {
        var wid = WorkerId;
        var list = await _db.IncConnections
            .Where(c => c.WorkerId == wid && !c.IsDeleted)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
        return View(list);
    }

    // New Installer — select connection + upload document.
    public IActionResult Create() => View(new IncConnection());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(IncConnection model, IFormFile? document)
    {
        model.Id = 0;
        model.WorkerId = WorkerId;
        model.Status = "Pending";
        model.CreatedAt = DateTime.UtcNow;
        model.CreatedBy = "worker-" + WorkerId;
        model.IsDeleted = false;

        if (document != null && document.Length > 0)
        {
            var (ok, path, _) = await _fileUpload.UploadAsync(document, $"inc/{WorkerId}");
            if (ok) model.DocumentPath = path;
        }

        _db.IncConnections.Add(model);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Connection submitted. Status: Pending (awaiting admin approval).";
        return RedirectToAction(nameof(Index));
    }

    // INC marks a Pending connection as Complete (ready for admin approval).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkComplete(int id)
    {
        var wid = WorkerId;
        var c = await _db.IncConnections.FirstOrDefaultAsync(x => x.Id == id && x.WorkerId == wid);
        if (c != null && c.Status == "Pending")
        {
            c.Status = "Complete";
            c.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Marked as Complete. Awaiting admin approval.";
        }
        return RedirectToAction(nameof(Index));
    }
}