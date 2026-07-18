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
/// User-side site survey flow.
/// 1. Download blank survey form (PDF/printable HTML)
/// 2. Fill it offline (or onsite)
/// 3. Upload filled form + roof photo + GPS photo
/// </summary>
[Area("SolarPanelUserPanel")]
[Authorize(Roles = "User")]
public class SiteSurveyController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IFileUploadService _fileUpload;
    private readonly INotificationService _notifications;
    private readonly ISolarRequestService _requestService;
    private readonly UserManager<ApplicationUser> _userManager;

    public SiteSurveyController(
        IUnitOfWork uow,
        IFileUploadService fileUpload,
        INotificationService notifications,
        ISolarRequestService requestService,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _fileUpload = fileUpload;
        _notifications = notifications;
        _requestService = requestService;
        _userManager = userManager;
    }

    // GET: /User/SiteSurvey            → picks user's latest project
    // GET: /User/SiteSurvey/Index/5    → uses request id 5
    public async Task<IActionResult> Index(int? id)
    {
        var userId = _userManager.GetUserId(User)!;

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

        // Spec task 12: once PM Surya Ghar is approved, Meter Dispatch AND Site Survey
        // open together. PM approval advances the request to the MeterDispatch stage, so
        // the Site Survey unlocks from MeterDispatch onward (it no longer waits for the
        // request to reach the SiteSurvey stage specifically).
        if (req.CurrentStage < ProjectStatus.MeterDispatch)
        {
            TempData["Info"] = "Site survey unlocks after PM Surya Ghar is approved by admin.";
            return RedirectToAction("Status", "SolarRequest", new { id });
        }

        var surveys = await _uow.SiteSurveys.FindAsync(s => s.SolarRequestId == req.Id);
        ViewBag.Request = req;
        ViewBag.LatestSurvey = surveys.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        return View();
    }

    // GET: /User/SiteSurvey/DownloadForm/5  — generates a printable HTML form pre-filled with the user's info
    public async Task<IActionResult> DownloadForm(int id)
    {
        var userId = _userManager.GetUserId(User)!;
        var req = await _uow.SolarRequests.GetByIdAsync(id);
        if (req == null || !string.Equals(req.UserId?.Trim(), userId?.Trim(), StringComparison.OrdinalIgnoreCase)) return NotFound();

        var html = BuildSurveyFormHtml(req);
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        var fileName = $"SiteSurveyForm-{req.RequestNumber}.html";
        return File(bytes, "text/html", fileName);
    }

    // POST: /User/SiteSurvey/Upload — saves filled SOLFIT survey form
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(
        int requestId,
        // ── Section 1: General ──
        string? PropertyType,
        string? RoofAvailable,
        string? RoofType,
        string? RoofTypeOther,
        decimal? RoofTotalAreaSqft,
        // ── Section 2: Electrical ──
        string? DiscomName,
        string? DiscomOther,
        string? MeterType,
        string? GridType,
        // ── Section 3: Site Conditions ──
        string? ShadowOnRoof,
        string[]? Obstructions,
        string? RoofDirection,
        string? RoofDirectionOther,
        string? EarthingAvailable,
        string? InternetAvailable,
        // ── Section 4: Photos & Notes ──
        string? surveyNotes,
        IFormFile? roofPhoto,
        IFormFile? housePhoto)
    {
        var userId = _userManager.GetUserId(User)!;
        var req = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (req == null || !string.Equals(req.UserId?.Trim(), userId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Request not found.";
            return RedirectToAction("Index");
        }

        // Spec task 14: if "Other" DISCOM chosen, use the free-text value entered.
        if (string.Equals(DiscomName, "Other", StringComparison.OrdinalIgnoreCase))
            DiscomName = string.IsNullOrWhiteSpace(DiscomOther) ? null : DiscomOther.Trim();

        // Required-field server-side check (defence-in-depth alongside HTML required)
        // Note (spec task 13): Consumer Number / "K No." is no longer collected.
        if (string.IsNullOrWhiteSpace(PropertyType) ||
            string.IsNullOrWhiteSpace(RoofAvailable) ||
            string.IsNullOrWhiteSpace(RoofType) ||
            string.IsNullOrWhiteSpace(DiscomName) ||
            string.IsNullOrWhiteSpace(MeterType) ||
            string.IsNullOrWhiteSpace(GridType) ||
            string.IsNullOrWhiteSpace(ShadowOnRoof) ||
            string.IsNullOrWhiteSpace(RoofDirection) ||
            string.IsNullOrWhiteSpace(EarthingAvailable) ||
            string.IsNullOrWhiteSpace(InternetAvailable))
        {
            TempData["Error"] = "Please fill in all required fields before submitting the survey.";
            return RedirectToAction("Index", new { id = requestId });
        }

        string? roofPath = null, housePath = null;
        // Organise uploads by project: uploads/SCR-001/sitesurvey/roof|house/<file>
        if (roofPhoto != null)
        {
            var (ok, path, _) = await _fileUpload.UploadAsync(roofPhoto, $"{req.RequestNumber}/sitesurvey/roof");
            if (ok) roofPath = path;
        }
        // Spec task 16: House Photo replaces the old GPS photo. We keep storing it in
        // the GpsPhotoPath column (repurposed) to avoid a schema change.
        if (housePhoto != null)
        {
            var (ok, path, _) = await _fileUpload.UploadAsync(housePhoto, $"{req.RequestNumber}/sitesurvey/house");
            if (ok) housePath = path;
        }

        var obstructionsCsv = Obstructions != null && Obstructions.Length > 0
            ? string.Join(",", Obstructions)
            : null;

        // Upsert pattern — if a non-approved survey already exists for this request,
        // update it; otherwise create a new row.
        var existing = (await _uow.SiteSurveys
                            .FindAsync(s => s.SolarRequestId == requestId && !s.IsCompleted))
                       .OrderByDescending(s => s.CreatedAt)
                       .FirstOrDefault();

        if (existing != null)
        {
            existing.PropertyType        = PropertyType;
            existing.RoofAvailable       = RoofAvailable;
            existing.RoofType            = RoofType;
            existing.RoofTypeOther       = RoofTypeOther;
            existing.RoofTotalAreaSqft   = RoofTotalAreaSqft;
            existing.DiscomName          = DiscomName;
            existing.ConsumerNumber      = null;   // K No. no longer collected (spec task 13)
            existing.MeterType           = MeterType;
            existing.GridType            = GridType;
            existing.ShadowOnRoof        = ShadowOnRoof;
            existing.Obstructions        = obstructionsCsv;
            existing.RoofDirection       = RoofDirection;
            existing.RoofDirectionOther  = RoofDirectionOther;
            existing.EarthingAvailable   = EarthingAvailable;
            existing.InternetAvailable   = InternetAvailable;
            existing.SurveyNotes         = surveyNotes;
            if (roofPath  != null) existing.RoofPhotoPath = roofPath;
            if (housePath != null) existing.GpsPhotoPath  = housePath;  // repurposed as House Photo
            existing.OperationsRemark    = null;  // clear prior reject reason — this is a fresh re-submission
            existing.UpdatedAt           = DateTime.UtcNow;
            existing.UpdatedBy           = userId;
            await _uow.SaveChangesAsync();
        }
        else
        {
            var survey = new SiteSurvey
            {
                SolarRequestId       = requestId,
                AssignedToUserId     = userId,
                SurveyDate           = DateTime.UtcNow,
                PropertyType         = PropertyType,
                RoofAvailable        = RoofAvailable,
                RoofType             = RoofType,
                RoofTypeOther        = RoofTypeOther,
                RoofTotalAreaSqft    = RoofTotalAreaSqft,
                DiscomName           = DiscomName,
                MeterType            = MeterType,
                GridType             = GridType,
                ShadowOnRoof         = ShadowOnRoof,
                Obstructions         = obstructionsCsv,
                RoofDirection        = RoofDirection,
                RoofDirectionOther   = RoofDirectionOther,
                EarthingAvailable    = EarthingAvailable,
                InternetAvailable    = InternetAvailable,
                SurveyNotes          = surveyNotes,
                RoofPhotoPath        = roofPath,
                GpsPhotoPath         = housePath,   // repurposed as House Photo (spec task 16)
                IsCompleted          = false,  // admin must approve
                CreatedAt            = DateTime.UtcNow,
                CreatedBy            = userId
            };
            await _uow.SiteSurveys.AddAsync(survey);
            await _uow.SaveChangesAsync();
        }

        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = userId,
            SolarRequestId = requestId,
            Title = "Site Survey submitted",
            Message = "Your filled site survey has been uploaded. Admin will verify shortly.",
            NotificationType = "SiteSurvey"
        });

        TempData["Success"] = "Site survey submitted successfully. Awaiting admin approval.";
        return RedirectToAction("Index", "Dashboard");
    }

    private static string BuildSurveyFormHtml(SolarRequest req)
    {
        // Printable, fillable HTML survey form. User prints/fills/re-uploads.
        var sb = new System.Text.StringBuilder();
        sb.Append("""
<!doctype html><html><head><meta charset='utf-8'/><title>Site Survey Form</title>
<style>
  body { font-family: Arial, sans-serif; max-width: 800px; margin: 24px auto; color: #222; }
  h1 { text-align: center; border-bottom: 2px solid #f59e0b; padding-bottom: 8px; }
  h3 { background: #fef3c7; padding: 6px 10px; margin-top: 24px; }
  table { width: 100%; border-collapse: collapse; margin: 8px 0; }
  td, th { border: 1px solid #999; padding: 6px 10px; vertical-align: top; }
  .field { height: 28px; }
  .sig { height: 80px; }
  .footer { margin-top: 24px; font-size: 12px; color: #666; text-align: center; }
  @media print { .noprint { display: none; } }
</style></head><body>
<button class='noprint' onclick='window.print()' style='padding:8px 16px;background:#f59e0b;color:white;border:0;border-radius:6px;cursor:pointer'>Print / Save as PDF</button>
""");
        sb.Append($"<h1>Site Survey Form — {req.RequestNumber}</h1>");
        sb.Append("<h3>Applicant Information</h3><table>");
        sb.Append($"<tr><th width='30%'>Applicant Name</th><td>{req.ApplicantName}</td></tr>");
        sb.Append($"<tr><th>Mobile</th><td>{req.MobileNumber}</td></tr>");
        sb.Append($"<tr><th>Email</th><td>{req.Email}</td></tr>");
        sb.Append($"<tr><th>Address</th><td>{req.Address}</td></tr>");
        sb.Append($"<tr><th>City / State / PIN</th><td>{req.City} / {req.State} / {req.PinCode}</td></tr>");
        sb.Append($"<tr><th>Plan</th><td>{req.SelectedPlan} ({req.KVCapacity} kV, {req.ConnectionType})</td></tr>");
        sb.Append("</table>");

        sb.Append("<h3>Site Details (Fill on site)</h3><table>");
        sb.Append("<tr><th width='40%'>Roof Type (RCC / Tin / Other)</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Roof Area Available (sq ft)</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Roof Orientation (N / S / E / W)</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Roof Tilt / Slope</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Shadow / Obstruction Notes</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Distance Roof → Meter (mtr)</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Existing Sanctioned Load (kW)</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Phase (Single / Three)</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Earthing Available</th><td class='field'></td></tr>");
        sb.Append("</table>");

        sb.Append("<h3>GPS / Coordinates</h3><table>");
        sb.Append("<tr><th width='40%'>Latitude</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Longitude</th><td class='field'></td></tr>");
        sb.Append("<tr><th>Plus Code</th><td class='field'></td></tr>");
        sb.Append("</table>");

        sb.Append("<h3>Additional Notes</h3>");
        sb.Append("<div style='border:1px solid #999; min-height: 120px; padding: 8px'></div>");

        sb.Append("<h3>Sign-off</h3><table>");
        sb.Append("<tr><th width='40%'>Applicant Signature</th><td class='sig'></td></tr>");
        sb.Append("<tr><th>Surveyor Signature</th><td class='sig'></td></tr>");
        sb.Append("<tr><th>Date</th><td class='field'></td></tr>");
        sb.Append("</table>");

        sb.Append("<div class='footer'>Generated by Solar Portal — fill, sign, photograph or scan, then re-upload.</div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
