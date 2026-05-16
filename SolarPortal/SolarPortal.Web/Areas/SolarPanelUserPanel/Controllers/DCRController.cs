using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Controllers;

/// <summary>
/// User panel — DCR (Daily Consumption / Domestic Consumer Registration) upload.
/// Domestic connections only. Final step before Completion.
/// </summary>
[Area("SolarPanelUserPanel")]
[Authorize(Roles = "User")]
public class DCRController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IFileUploadService _fileUpload;
    private readonly ISolarRequestService _requestService;
    private readonly INotificationService _notifications;
    private readonly UserManager<ApplicationUser> _userManager;

    public DCRController(
        IUnitOfWork uow,
        IFileUploadService fileUpload,
        ISolarRequestService requestService,
        INotificationService notifications,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _fileUpload = fileUpload;
        _requestService = requestService;
        _notifications = notifications;
        _userManager = userManager;
    }

    // GET: /User/DCR/Upload          → picks user's latest project (Domestic only)
    // GET: /User/DCR/Upload/5        → uses request id 5
    public async Task<IActionResult> Upload(int? id)
    {
        var userId = _userManager.GetUserId(User)!;

        if (id == null || id.Value == 0)
        {
            // Prefer the latest Domestic project; if none, pick any
            var mine = (await _uow.SolarRequests.FindAsync(r => r.UserId == userId)).ToList();
            var latest = mine.Where(r => r.ConnectionType == ConnectionType.Domestic)
                             .OrderByDescending(r => r.CreatedAt).FirstOrDefault()
                         ?? mine.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            if (latest == null)
            {
                TempData["Error"] = "You don't have any active solar request yet. Please create one first.";
                return RedirectToAction("Create", "SolarRequest");
            }
            id = latest.Id;
        }

        var req = await _uow.SolarRequests.GetByIdAsync(id.Value);
        if (req == null || req.UserId != userId) return NotFound();

        if (req.ConnectionType != ConnectionType.Domestic)
        {
            TempData["Error"] = "DCR upload applies to Domestic connections only. Commercial connections complete after installation.";
            return RedirectToAction("Status", "SolarRequest", new { id });
        }
        if (req.CurrentStage < ProjectStatus.DCRUpdate)
        {
            TempData["Error"] = "DCR unlocks after Installation is complete.";
            return RedirectToAction("Status", "SolarRequest", new { id });
        }

        var existing = await _uow.DCRDocuments.FindAsync(d => d.SolarRequestId == req.Id);
        ViewBag.Request = req;
        ViewBag.Existing = existing.OrderByDescending(d => d.CreatedAt).FirstOrDefault();
        return View();
    }

    // POST: /User/DCR/Submit (AJAX)
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int requestId, string dcrNumber, DateTime? dcrDate, string? remark, IFormFile dcrDoc)
    {
        var userId = _userManager.GetUserId(User)!;
        var req = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (req == null || req.UserId != userId)
            return Json(new { success = false, message = "Request not found" });

        if (req.ConnectionType != ConnectionType.Domestic)
            return Json(new { success = false, message = "DCR is for Domestic connections only" });

        if (string.IsNullOrWhiteSpace(dcrNumber))
            return Json(new { success = false, message = "DCR number is required" });

        if (dcrDoc == null || dcrDoc.Length == 0)
            return Json(new { success = false, message = "Please attach the DCR document" });

        var (ok, path, error) = await _fileUpload.UploadAsync(dcrDoc, $"dcr/{requestId}");
        if (!ok)
            return Json(new { success = false, message = error });

        var doc = new DCRDocument
        {
            SolarRequestId = requestId,
            DCRNumber = dcrNumber,
            DCRDate = dcrDate ?? DateTime.UtcNow,
            DocumentPath = path,
            Remark = remark,
            IsVerified = false // Admin will verify
        };
        await _uow.DCRDocuments.AddAsync(doc);
        await _uow.SaveChangesAsync();

        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = userId,
            SolarRequestId = requestId,
            Title = "DCR submitted",
            Message = $"DCR {dcrNumber} uploaded — awaiting admin verification.",
            NotificationType = "DCR"
        });

        return Json(new { success = true, message = "DCR uploaded. Admin will verify and mark the project complete." });
    }
}
