using SolarPortal.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Controllers;

/// <summary>
/// "My Reports" — single page where the user can see their entire submission
/// history across all modules with current status. Added per spec:
///
///   "user panel pr bhi show karwao all reports"
///
/// We don't try to merge everything into one giant list. Instead, the view
/// gets typed sections (requests / payments / pm-docs / site-survey / dispatch
/// / dcr) and renders each as its own table. Filter param picks one section
/// (default = all sections shown).
/// </summary>
[Area("SolarPanelUserPanel")]
[Authorize]
public class ReportsController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILegacyProductRequestService _legacyBridge;

    public ReportsController(IUnitOfWork uow, UserManager<ApplicationUser> userManager,
        IWebHostEnvironment env, IConfiguration config,
        ILegacyProductRequestService legacyBridge)
    {
        _uow = uow;
        _userManager = userManager;
        _env = env;
        _config = config;
        _legacyBridge = legacyBridge;
    }

    // Resolve a stored relative path (e.g. "/uploads/payments/abc.jpg") to a
    // physical file under wwwroot and check whether it actually exists on
    // disk. Older test records sometimes reference files that have since
    // been cleaned up — without this check the user gets a generic 404 page
    // when clicking the View link. With it, we render "File missing" inline.
    private bool FileExistsOnDisk(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return false;
        try
        {
            var rel = storedPath.Replace("\\", "/").TrimStart('/');
            if (rel.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("wwwroot/".Length);
            var full = Path.Combine(_env.WebRootPath ?? "", rel.Replace('/', Path.DirectorySeparatorChar));
            return System.IO.File.Exists(full);
        }
        catch { return false; }
    }

    // GET: /User/Reports
    public async Task<IActionResult> Index(string? filter)
    {
        var userId = _userManager.GetUserId(User)!;
        var f = (filter ?? "all").ToLowerInvariant();

        // Pull every solar request belonging to this user. All downstream
        // entities (payments / pm-docs / site-surveys / dispatch / DCR) are
        // keyed by SolarRequestId so we filter the related tables in-memory
        // off that list rather than running a join per table.
        var myRequests = (await _uow.SolarRequests.FindAsync(r => r.UserId == userId))
                         // Hide the unfilled auto-stub (registered but no plan/
                         // product/amount yet) — My Reports should only list real
                         // submitted requests. A Completed ₹0 project still counts.
                         .Where(r =>
                             r.SolarProjectId != null ||
                             r.ExternalProductId != null ||
                             r.KVCapacity != 0m ||
                             r.PlanAmount != 0m ||
                             r.CurrentStage == ProjectStatus.Completed)
                         .OrderByDescending(r => r.CreatedAt)
                         .ToList();
        var reqIds = myRequests.Select(r => r.Id).ToHashSet();

        // Apply the high-level filter to each section. Empty filter shows all.
        var payments = (await _uow.Payments.FindAsync(p => p.UserId == userId)).ToList();

        // Per-request verified-paid map (from the UNFILTERED payment list so figures
        // stay correct regardless of the section filter).
        var paidByReq = myRequests.ToDictionary(
            r => r.Id,
            r => payments.Where(p => p.SolarRequestId == r.Id && p.IsVerified).Sum(p => p.Amount));

        // Point 1: an Already-Active member already paid for the cPanel order that
        // activated their ID, and that money sits against this project. It is not
        // a Payment row, so without this the report bills them for it again -
        // "₹19,900 due" on a project they have in fact paid in full.
        //
        // Folded INTO paidByReq so the Paid column, the Due column and anything
        // derived from them are corrected in one place and cannot disagree.
        // depositByReq is passed separately only so the row can name the money.
        var depositByReq = new Dictionary<int, decimal>();
        foreach (var r in myRequests)
        {
            var dep = await _legacyBridge.GetDepositForRequestAsync(r.RequestType, r.UserId);
            if (dep <= 0m) continue;
            depositByReq[r.Id] = dep;
            paidByReq[r.Id] = paidByReq[r.Id] + dep;
        }

        ViewBag.PaidByRequest = paidByReq;
        ViewBag.DepositByRequest = depositByReq;

        if (f == "pending")  payments = payments.Where(p => p.Status == PaymentStatus.Pending).ToList();
        if (f == "approved") payments = payments.Where(p => p.Status == PaymentStatus.Completed).ToList();
        if (f == "rejected") payments = payments.Where(p => p.Status == PaymentStatus.Rejected).ToList();

        var pmDocs = (await _uow.PMDocuments.FindAsync(d => reqIds.Contains(d.SolarRequestId))).ToList();
        if (f == "pending")  pmDocs = pmDocs.Where(d => d.Status == ApprovalStatus.Pending).ToList();
        if (f == "approved") pmDocs = pmDocs.Where(d => d.Status == ApprovalStatus.Approved).ToList();
        if (f == "rejected") pmDocs = pmDocs.Where(d => d.Status == ApprovalStatus.Rejected).ToList();

        var siteSurveys = (await _uow.SiteSurveys.FindAsync(s => reqIds.Contains(s.SolarRequestId))).ToList();
        if (f == "pending")  siteSurveys = siteSurveys.Where(s => s.ApprovalStatus == ApprovalStatus.Pending && !s.IsCompleted).ToList();
        if (f == "approved") siteSurveys = siteSurveys.Where(s => s.ApprovalStatus == ApprovalStatus.Approved || s.IsCompleted).ToList();
        if (f == "rejected") siteSurveys = siteSurveys.Where(s => s.ApprovalStatus == ApprovalStatus.Rejected).ToList();

        var meters       = (await _uow.MeterDispatches.FindAsync(m => reqIds.Contains(m.SolarRequestId))).ToList();
        var materials    = (await _uow.MaterialDispatches.FindAsync(m => reqIds.Contains(m.SolarRequestId))).ToList();
        var installations = (await _uow.Installations.FindAsync(i => reqIds.Contains(i.SolarRequestId))).ToList();

        var dcrDocs = (await _uow.DCRDocuments.FindAsync(d => reqIds.Contains(d.SolarRequestId))).ToList();
        if (f == "pending")  dcrDocs = dcrDocs.Where(d => d.ApprovalStatus == ApprovalStatus.Pending).ToList();
        if (f == "approved") dcrDocs = dcrDocs.Where(d => d.ApprovalStatus == ApprovalStatus.Approved).ToList();
        if (f == "rejected") dcrDocs = dcrDocs.Where(d => d.ApprovalStatus == ApprovalStatus.Rejected).ToList();

        ViewBag.Filter      = f;
        ViewBag.Requests    = myRequests;
        ViewBag.Payments    = payments;
        ViewBag.PMDocs      = pmDocs;
        ViewBag.SiteSurveys = siteSurveys;
        ViewBag.Meters      = meters;
        ViewBag.Materials   = materials;
        ViewBag.Installations = installations;
        ViewBag.DcrDocs     = dcrDocs;

        // Lookup map so the view can render Request # next to each row
        ViewBag.RequestLookup = myRequests.ToDictionary(r => r.Id, r => r.RequestNumber);

        // Build a "does the file exist on disk?" map for every path referenced
        // in this report. Older test records can point to files that have since
        // been cleaned up — without this map the user clicks "View" and lands
        // on the browser's generic 404. With it, the view renders "File missing"
        // inline so the user knows to ignore that link.
        var allPaths = payments.Select(p => p.ReceiptImagePath)
            .Concat(pmDocs.Select(d => d.FilePath))
            .Concat(meters.Select(m => m.DispatchDocumentPath))
            .Concat(materials.Select(m => m.DispatchDocumentPath))
            .Concat(installations.Select(i => i.CompletionPhotoPath))
            .Concat(dcrDocs.Select(d => d.DocumentPath))
            .Concat(siteSurveys.Select(s => s.RoofPhotoPath))
            .Concat(siteSurveys.Select(s => s.GpsPhotoPath))
            .Concat(siteSurveys.Select(s => s.SurveyPhotoPath))
            .Concat(myRequests.Select(r => r.LightBillProofPath))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToList();
        ViewBag.FileExists = allPaths!.ToDictionary(p => p!, p => FileExistsOnDisk(p));

        // Resolved URL per stored path. Files uploaded by the ADMIN panel physically live
        // on the admin server (shared DB, separate wwwroot) - when a path is not found
        // locally, point it at the admin site instead of rendering "File missing".
        var adminBase = (_config["AdminPanelBaseUrl"] ?? "https://solaradmin.solfit.in").TrimEnd('/');
        string NormalizeUrl(string storedPath)
        {
            var rel = storedPath.Replace("\\", "/").TrimStart('/');
            if (rel.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("wwwroot/".Length);
            return "/" + rel;
        }
        ViewBag.FileUrl = allPaths!.ToDictionary(
            p => p!,
            p => FileExistsOnDisk(p) ? NormalizeUrl(p!) : adminBase + NormalizeUrl(p!));

        return View();
    }
}
