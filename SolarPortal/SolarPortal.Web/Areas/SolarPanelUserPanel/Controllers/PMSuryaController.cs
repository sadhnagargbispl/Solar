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
/// User panel — PM Surya Ghar document upload.
/// Workflow stage 4 (after Payment is approved).
/// </summary>
[Area("SolarPanelUserPanel")]
[Authorize(Roles = "User")]
public class PMSuryaController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IPMDocumentService _pmDocs;
    private readonly IFileUploadService _fileUpload;
    private readonly ISolarRequestService _requestService;
    private readonly INotificationService _notifications;
    private readonly UserManager<ApplicationUser> _userManager;

    public PMSuryaController(
        IUnitOfWork uow,
        IPMDocumentService pmDocs,
        IFileUploadService fileUpload,
        ISolarRequestService requestService,
        INotificationService notifications,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _pmDocs = pmDocs;
        _fileUpload = fileUpload;
        _requestService = requestService;
        _notifications = notifications;
        _userManager = userManager;
    }

    // GET: /User/PMSurya/Upload         → picks user's latest project
    // GET: /User/PMSurya/Upload/5       → uses request id 5
    public async Task<IActionResult> Upload(int? id)
    {
        var userId = _userManager.GetUserId(User)!;

        // Auto-pick the user's most recent project if no id provided
        if (id == null || id.Value == 0)
        {
            var mine = await _uow.SolarRequests.FindAsync(r => r.UserId == userId);
            var latest = mine.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            if (latest == null)
            {
                TempData["Error"] = "You don't have any active solar request yet. Please create one first.";
                return RedirectToAction("Create", "SolarRequest");
            }
            id = latest.Id;
        }

        var req = await _uow.SolarRequests.GetByIdAsync(id.Value);
        if (req == null || !string.Equals(req.UserId?.Trim(), userId?.Trim(), StringComparison.OrdinalIgnoreCase)) return NotFound();

        // ===== PM Surya Ghar gate =====
        // Per spec (task 6): "Payment pura nahi aaye to bhi sabhi task complete ho sakte he."
        // Incomplete / partial payment must NOT block the workflow anymore. The only gate we
        // keep is the stage gate — admin advances the request to the PM Surya stage once the
        // request itself is approved. The earlier full-payment requirement has been removed.
        if (req.CurrentStage < ProjectStatus.PMSurvey)
        {
            TempData["Info"] = "PM Surya Ghar upload unlocks after your request is approved by admin.";
            return RedirectToAction("Status", "SolarRequest", new { id });
        }

        var docs = await _pmDocs.GetByRequestIdAsync(req.Id);
        ViewBag.Request = req;
        ViewBag.Documents = docs;
        return View();
    }

    // POST: /User/PMSurya/UploadDocument  (AJAX)
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadDocument(int requestId, string documentType, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return Json(new { success = false, message = "Please select a file" });

        var userId = _userManager.GetUserId(User)!;
        var req = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (req == null || !string.Equals(req.UserId?.Trim(), userId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Json(new { success = false, message = "Request not found" });

        // ===== Gate: only the stage gate remains (mirrors GET Upload) =====
        // Per spec (task 6) incomplete payment no longer blocks uploads.
        if (req.CurrentStage < ProjectStatus.PMSurvey)
            return Json(new { success = false, message = "PM Surya Ghar is locked until admin approves your request." });

        // Save the file
        // Organise uploads by project: uploads/SCR-001/pmsurya/<file>
        var (ok, path, error) = await _fileUpload.UploadAsync(file, $"{req.RequestNumber}/pmsurya");
        if (!ok)
            return Json(new { success = false, message = error });

        if (!Enum.TryParse<DocumentType>(documentType, out var docType))
            docType = DocumentType.PMSuryagramDocument;

        await _pmDocs.UploadDocumentAsync(
            solarRequestId: requestId,
            documentType: docType,
            fileName: Path.GetFileNameWithoutExtension(file.FileName),
            filePath: path!,
            contentType: file.ContentType,
            fileSize: file.Length);

        // Notify admin
        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = userId,
            SolarRequestId = requestId,
            Title = "PM Surya Ghar document uploaded",
            Message = $"Document '{file.FileName}' is awaiting admin verification.",
            NotificationType = "PMSurya"
        });

        return Json(new { success = true, message = "Document uploaded. Waiting for admin verification.", filePath = path });
    }

    // POST: /User/PMSurya/SaveLoanOption  — user picks Loan / Without Loan (spec task 7)
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveLoanOption(int requestId, string loanOption)
    {
        var userId = _userManager.GetUserId(User)!;
        var req = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (req == null || !string.Equals(req.UserId?.Trim(), userId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Json(new { success = false, message = "Request not found" });

        if (loanOption != "Loan" && loanOption != "WithoutLoan")
            return Json(new { success = false, message = "Invalid option" });

        req.PMSuryaLoanOption = loanOption;
        req.UpdatedAt = DateTime.UtcNow;
        req.UpdatedBy = userId;
        _uow.SolarRequests.Update(req);
        await _uow.SaveChangesAsync();

        return Json(new { success = true, message = "Loan preference saved." });
    }

    // POST: /User/PMSurya/Submit  — user clicks "Submit for verification"
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int requestId)
    {
        var userId = _userManager.GetUserId(User)!;
        var req = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (req == null || !string.Equals(req.UserId?.Trim(), userId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Json(new { success = false, message = "Request not found" });

        var docs = (await _pmDocs.GetByRequestIdAsync(requestId)).ToList();
        if (!docs.Any())
            return Json(new { success = false, message = "Upload at least one PM Surya Ghar document first." });

        // Just record an "awaiting admin" notification — the stage advances when admin approves.
        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = userId,
            SolarRequestId = requestId,
            Title = "PM Surya Ghar submitted",
            Message = $"{docs.Count} document(s) submitted. Admin will verify shortly.",
            NotificationType = "PMSurya"
        });

        return Json(new { success = true, message = "Submitted for admin verification." });
    }
}
