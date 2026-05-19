using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;
using SolarPortal.Web.ViewModels;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Controllers;

[Area("SolarPanelUserPanel")]
[Authorize(Roles = "User")]
public class SolarRequestController : Controller
{
    private readonly ISolarRequestService _solarRequestService;
    private readonly IPaymentService _paymentService;
    private readonly IDocumentService _documentService;
    private readonly IFileUploadService _fileUploadService;
    private readonly ISolarProjectService _solarProjectService;
    private readonly INotificationService _notificationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;

    public SolarRequestController(
        ISolarRequestService solarRequestService,
        IPaymentService paymentService,
        IDocumentService documentService,
        IFileUploadService fileUploadService,
        ISolarProjectService solarProjectService,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        IConfiguration config)
    {
        _solarRequestService = solarRequestService;
        _paymentService = paymentService;
        _documentService = documentService;
        _fileUploadService = fileUploadService;
        _solarProjectService = solarProjectService;
        _notificationService = notificationService;
        _userManager = userManager;
        _db = db;
        _config = config;
    }

    // GET: New Request Form (multi-step)
    public async Task<IActionResult> Create()
    {
        var userId = _userManager.GetUserId(User)!;
        var existing = await _solarRequestService.GetByUserIdAsync(userId);

        if (existing.IsSuccess && existing.Data != null && existing.Data.Any())
        {
            // STRICT LIFETIME RULE — one IdNo, one solar request, forever.
            // Across all 3 modes (With Activation, Only Solar Without Activation,
            // Already Active Only Request) and all statuses (Pending / Approved /
            // Rejected / Completed) the user is bound to their single request.
            //
            // ONLY EXCEPTION: the auto-stub created at first login is meant to be
            // FILLED IN — that's not "creating a new request", it's completing the
            // initial submission. Identified by:
            //   • Stage = Registration or ProductSelection
            //   • ApprovalStatus = Pending
            //   • No SolarProject picked yet
            var latest = existing.Data
                .OrderByDescending(r => r.CreatedAt)
                .First();

            var isAutoStub = (latest.CurrentStage == ProjectStatus.Registration ||
                              latest.CurrentStage == ProjectStatus.ProductSelection) &&
                             latest.ApprovalStatus == ApprovalStatus.Pending &&
                             latest.SolarProjectId == null;

            if (isAutoStub)
            {
                // Allow the form to open so the user can pick a plan + fill the stub
                ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
                var prefill = new CreateSolarRequestViewModel
                {
                    ApplicantName  = latest.ApplicantName ?? string.Empty,
                    MobileNumber   = latest.MobileNumber ?? string.Empty,
                    Email          = latest.Email ?? string.Empty,
                    Address        = latest.Address ?? string.Empty,
                    City           = latest.City ?? string.Empty,
                    State          = latest.State ?? string.Empty,
                    PinCode        = latest.PinCode ?? string.Empty,
                    AadharNumber   = latest.AadharNumber,
                    PANNumber      = latest.PANNumber,
                    ConnectionType = latest.ConnectionType,
                    KVCapacity     = latest.KVCapacity,
                    SolarProjectId = latest.SolarProjectId,
                    SelectedPlan   = latest.SelectedPlan,
                    PlanAmount     = latest.RequestedAmount,
                    RequestType    = latest.RequestType
                };
                ViewBag.EditingRequestId = latest.Id;
                return View(prefill);
            }

            // Any other state → BLOCK forever, redirect to Status.
            string statusLabel = latest.ApprovalStatus switch
            {
                ApprovalStatus.Approved when latest.CurrentStage == ProjectStatus.Completed => "completed",
                ApprovalStatus.Approved => "approved and in progress",
                ApprovalStatus.Rejected => "rejected",
                _                       => "in progress"
            };

            TempData["Warning"] = $"You already have a solar request ({latest.RequestNumber}) which is {statusLabel}. " +
                                   "Only one solar request is allowed per user. Please track its status here.";
            return RedirectToAction(nameof(Status), new { id = latest.Id });
        }

        ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
        return View(new CreateSolarRequestViewModel());
    }

    // POST: Step 1 - Personal Info
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateSolarRequestViewModel model)
    {
        // Normalize PAN to uppercase (regex accepts both cases for user convenience)
        if (!string.IsNullOrWhiteSpace(model.PANNumber))
            model.PANNumber = model.PANNumber.Trim().ToUpperInvariant();

        // Server-side enforcement.
        // Q1: Approved request — BLOCK forever.
        var userIdEarly = _userManager.GetUserId(User)!;
        var existingEarly = await _solarRequestService.GetByUserIdAsync(userIdEarly);
        SolarRequestDto? stubToUpdate = null;
        if (existingEarly.IsSuccess && existingEarly.Data != null && existingEarly.Data.Any())
        {
            // STRICT LIFETIME RULE — same as GET. Only the auto-stub can be updated.
            var latest = existingEarly.Data
                .OrderByDescending(r => r.CreatedAt)
                .First();

            var isAutoStub = (latest.CurrentStage == ProjectStatus.Registration ||
                              latest.CurrentStage == ProjectStatus.ProductSelection) &&
                             latest.ApprovalStatus == ApprovalStatus.Pending &&
                             latest.SolarProjectId == null;

            if (isAutoStub)
            {
                stubToUpdate = latest;   // user is filling the stub — allow update
            }
            else
            {
                TempData["Error"] = $"You already have a solar request ({latest.RequestNumber}). " +
                                     "Only one solar request is allowed per user.";
                return RedirectToAction(nameof(Status), new { id = latest.Id });
            }
        }

        // ─── UTR duplicate check ─────────────────────────────────────────
        // The same UTR / Transaction No. must not exist anywhere in the system.
        // We cross-check against:
        //   1. walletreq.chqno — live cooperative master table
        //   2. Payments.UTRNumber — our own payment ledger
        if (model.PaymentAmount > 0 && !string.IsNullOrWhiteSpace(model.PaymentUTR))
        {
            var dupReason = await CheckUtrDuplicateAsync(model.PaymentUTR.Trim());
            if (dupReason != null)
            {
                ModelState.AddModelError(nameof(model.PaymentUTR), dupReason);
            }
        }

        if (!ModelState.IsValid)
        {
            ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
            return View(model);
        }

        // === Mode 2 and Mode 3: skip plan selection ===
        // - Mode 2 (OnlySolarWithoutActivation): admin will assign a project after approval
        // - Mode 3 (AlreadyActiveOnlyRequest): inherits from user's existing active project
        if (model.RequestType == RequestType.AlreadyActiveOnlyRequest)
        {
            // Look for the user's earlier REAL request (one that has a SolarProjectId
            // set — not the auto-stub which has SolarProjectId == null). Without this
            // filter the auto-stub itself becomes the "basis" and PlanAmount ends up 0.
            var mine = existingEarly.IsSuccess && existingEarly.Data != null
                ? existingEarly.Data
                    .Where(r => r.SolarProjectId.HasValue && r.RequestedAmount > 0)
                    .OrderByDescending(r => r.CreatedAt)
                    .ToList()
                : new List<SolarRequestDto>();
            var basis = mine.FirstOrDefault();

            if (basis != null)
            {
                // Carry forward existing project info
                model.SolarProjectId = basis.SolarProjectId;
                model.SelectedPlan   = string.IsNullOrWhiteSpace(model.SelectedPlan)
                                        ? (basis.SelectedPlan ?? "Already Active — Only Request")
                                        : model.SelectedPlan;
                model.PlanAmount     = basis.RequestedAmount;
                model.KVCapacity     = basis.KVCapacity;
                // Preserve the user's currently chosen ConnectionType. Do not overwrite
                // with the prior request's value — they may legitimately be changing it.
            }
            else
            {
                // No prior real project. Auto-match a plan by KV so PlanAmount isn't 0.
                var matched = await FindMatchingPlanAsync(model.KVCapacity, model.ConnectionType);
                if (matched != null)
                {
                    model.SolarProjectId = matched.Id;
                    model.SelectedPlan   = string.IsNullOrWhiteSpace(model.SelectedPlan)
                                            ? $"Already Active — {matched.Name}"
                                            : model.SelectedPlan;
                    model.PlanAmount     = matched.ProjectAmount;
                }
                else
                {
                    model.SelectedPlan = "Already Active — Only Request (pending plan assignment)";
                }
            }
        }
        else if (model.RequestType == RequestType.OnlySolarWithoutActivation)
        {
            // Mode 2: try to auto-link a plan by KV so the user sees a real
            // Project Amount on the Status page. Admin can change the plan later.
            var matched = await FindMatchingPlanAsync(model.KVCapacity, model.ConnectionType);
            if (matched != null)
            {
                model.SolarProjectId = matched.Id;
                model.SelectedPlan   = $"Only Solar — {matched.Name}";
                model.PlanAmount     = matched.ProjectAmount;
                model.KVCapacity     = matched.SolarTypeKV;
                // Preserve the user's ConnectionType selection — don't override.
            }
            else
            {
                model.SolarProjectId = null;
                model.SelectedPlan   = "Only Solar — Without Activation (pending plan assignment)";
                if (model.PlanAmount < 0) model.PlanAmount = 0m;
                if (model.KVCapacity <= 0) model.KVCapacity = 0m;
            }
        }
        // If a SolarProject was picked (Mode 1), hydrate plan name + amount + kv from master
        else if (model.SolarProjectId.HasValue)
        {
            var project = await _solarProjectService.GetByIdAsync(model.SolarProjectId.Value);
            if (project != null)
            {
                model.SelectedPlan   = project.Name;
                model.PlanAmount     = project.ProjectAmount;
                model.KVCapacity     = project.SolarTypeKV;
                // Preserve the user's chosen ConnectionType — don't override with plan's.
            }
        }
        else
        {
            // Mode 1 but no plan picked yet — try to auto-match
            var matched = await FindMatchingPlanAsync(model.KVCapacity, model.ConnectionType);
            if (matched != null)
            {
                model.SolarProjectId = matched.Id;
                model.SelectedPlan   = matched.Name;
                model.PlanAmount     = matched.ProjectAmount;
            }
        }

        // SAFETY NET: if PlanAmount is still 0 but we have a KVCapacity, do one last lookup.
        // This catches edge cases where Mode/RequestType branches missed assigning a plan.
        if (model.PlanAmount <= 0 && model.KVCapacity > 0)
        {
            var lastChance = await FindMatchingPlanAsync(model.KVCapacity, model.ConnectionType);
            if (lastChance != null)
            {
                model.SolarProjectId ??= lastChance.Id;
                model.PlanAmount       = lastChance.ProjectAmount;
                if (string.IsNullOrWhiteSpace(model.SelectedPlan))
                    model.SelectedPlan = lastChance.Name;
            }
        }

        var userId = _userManager.GetUserId(User)!;
        var dto = new CreateSolarRequestDto
        {
            ApplicantName = model.ApplicantName,
            MobileNumber = model.MobileNumber,
            Email = model.Email,
            Address = model.Address,
            City = model.City,
            State = model.State,
            PinCode = model.PinCode,
            AadharNumber = model.AadharNumber,
            PANNumber = model.PANNumber,
            RequestType = model.RequestType,
            ConnectionType = model.ConnectionType,
            KVCapacity = model.KVCapacity,
            SolarProjectId = model.SolarProjectId,
            SelectedPlan = model.SelectedPlan,
            PlanAmount = model.PlanAmount
        };

        // If we're filling an auto-created stub, update it; else create new.
        ServiceResult<SolarRequestDto> result;
        if (stubToUpdate != null)
        {
            // Update the existing stub record with the form data
            var entity = await _db.SolarRequests.FirstOrDefaultAsync(r => r.Id == stubToUpdate.Id);
            if (entity != null)
            {
                entity.ApplicantName  = dto.ApplicantName;
                entity.MobileNumber   = dto.MobileNumber;
                entity.Email          = dto.Email;
                entity.Address        = dto.Address;
                entity.City           = dto.City;
                entity.State          = dto.State;
                entity.PinCode        = dto.PinCode;
                entity.AadharNumber   = dto.AadharNumber;
                entity.PANNumber      = dto.PANNumber;
                entity.RequestType    = dto.RequestType;
                entity.ConnectionType = dto.ConnectionType;
                entity.KVCapacity     = dto.KVCapacity;
                entity.SolarProjectId = dto.SolarProjectId;
                entity.SelectedPlan   = dto.SelectedPlan;
                entity.PlanAmount     = dto.PlanAmount;
                entity.CurrentStage   = ProjectStatus.Payment;  // ← advance to Payment stage
                entity.UpdatedAt      = DateTime.UtcNow;
                entity.UpdatedBy      = userId;
                await _db.SaveChangesAsync();

                // Re-fetch as DTO for downstream code
                var refreshed = await _solarRequestService.GetByIdAsync(stubToUpdate.Id);
                result = refreshed.IsSuccess && refreshed.Data != null
                    ? ServiceResult<SolarRequestDto>.Success(refreshed.Data)
                    : ServiceResult<SolarRequestDto>.Failure("Could not reload request after update");
            }
            else
            {
                result = ServiceResult<SolarRequestDto>.Failure("Stub request not found");
            }
        }
        else
        {
            result = await _solarRequestService.CreateAsync(dto, userId);
        }

        if (!result.IsSuccess)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error);
            ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
            return View(model);
        }

        TempData["Success"] = $"Request {result.Data!.RequestNumber} submitted successfully!";
        TempData["RequestId"] = result.Data.Id;

        // === Save first payment in the same submission (Solar Request + Payment combined) ===
        // Per spec: payment fields appear on the same Create form. After the request is saved,
        // we immediately persist the payment as Pending so admin can verify it.
        // Server enforces effective minimum and ≤ project-total cap.
        if (model.PaymentAmount > 0)
        {
            try
            {
                // Effective min = min(₹20,000, project total). For a ₹15,900 project,
                // the user can pay ₹15,900 (full) as the first payment — the ₹20K floor
                // does not block this since it's logically a complete payment.
                var hardMin      = PaymentService.MinimumPaymentThreshold;
                var effectiveMin = model.PlanAmount > 0 && model.PlanAmount < hardMin
                                    ? model.PlanAmount
                                    : hardMin;

                if (model.PaymentAmount < effectiveMin)
                {
                    TempData["Warning"] = model.PlanAmount > 0 && model.PlanAmount < hardMin
                        ? $"Request saved, but first payment of ₹{model.PaymentAmount:N0} is below the ₹{effectiveMin:N0} (full project amount). Please pay from the Payment page."
                        : $"Request saved, but first payment of ₹{model.PaymentAmount:N0} is below the ₹{hardMin:N0} minimum. Please add the remaining amount from the Payment page.";
                }
                else if (model.PlanAmount > 0 && model.PaymentAmount > model.PlanAmount)
                {
                    TempData["Warning"] = $"Request saved, but the entered payment ₹{model.PaymentAmount:N0} exceeds the project total ₹{model.PlanAmount:N0}. Payment was not recorded — please re-enter from the Payment page.";
                }
                else
                {
                    string? receiptPath = null;
                    if (model.PaymentReceipt != null)
                    {
                        var (ok, path, _) = await _fileUploadService.UploadAsync(model.PaymentReceipt, "payments");
                        if (ok) receiptPath = path;
                    }

                    var payDto = new CreatePaymentDto
                    {
                        SolarRequestId    = result.Data.Id,
                        UserId            = userId,
                        Amount            = model.PaymentAmount,
                        UTRNumber         = (model.PaymentUTR ?? "").Trim(),
                        PaymentDate       = model.PaymentDate ?? DateTime.UtcNow,
                        PaymentMethod     = string.IsNullOrWhiteSpace(model.PaymentMethod) ? "Online" : model.PaymentMethod,
                        ReceiptImagePath  = receiptPath
                    };
                    var payResult = await _paymentService.CreateAsync(payDto);
                    if (!payResult.IsSuccess)
                    {
                        TempData["Warning"] = "Request saved, but the first payment couldn't be recorded: " +
                                              (payResult.Message ?? payResult.Errors.FirstOrDefault() ?? "unknown error");
                    }
                }
            }
            catch (Exception ex)
            {
                TempData["Warning"] = "Request saved, but payment save failed: " + ex.Message;
            }
        }

        // === Mode 2 post-save redirect ===
        // Per spec: when the request is created under "Only Solar — Without Activation" mode,
        // the user is sent to the main portal where the full solar summary lives.
        // The request is saved first, then we redirect — saving is unconditional.
        if (model.RequestType == RequestType.OnlySolarWithoutActivation)
        {
            var url = _config["ExternalRedirects:Mode2SolarSummaryUrl"];
            if (!string.IsNullOrWhiteSpace(url))
                return Redirect(url);
            // If URL is not configured, fall through to the normal Upload Documents page
        }

        return RedirectToAction("Upload", "PMSurya", new { id = result.Data.Id });
    }

    // Helper: find a SolarProject matching the given KV and connection type.
    // Used by Mode 2 (and as fallback for Mode 1) to auto-fetch the project amount
    // when the user hasn't explicitly picked a plan card.
    private async Task<SolarProjectDto?> FindMatchingPlanAsync(decimal kv, ConnectionType conn)
    {
        var all = await _solarProjectService.GetAllAsync(activeOnly: true);
        // Require KV to match — picking a wildly different plan just because
        // the connection type happens to match would silently misprice the request.
        // Prefer exact KV+connection, fall back to KV-only.
        return all.FirstOrDefault(p => p.SolarTypeKV == kv && p.ConnectionType == conn)
            ?? all.FirstOrDefault(p => p.SolarTypeKV == kv);
    }

    /// <summary>
    /// Cross-checks a UTR / Transaction number against both:
    ///   1. walletreq.chqno   — live cooperative DB master
    ///   2. Payments.UTRNumber — our own ledger
    /// Returns a user-friendly error message if the UTR is already in use, else null.
    /// </summary>
    private async Task<string?> CheckUtrDuplicateAsync(string utr)
    {
        if (string.IsNullOrWhiteSpace(utr)) return null;
        var trimmed = utr.Trim();

        // 1. walletreq master (raw SQL — table is outside our entity model).
        //    Use a SEPARATE SqlConnection — never wrap the DbContext's own
        //    connection in a `using` block, because disposing it leaves EF
        //    Core's DbContext with no ConnectionString for later queries.
        try
        {
            var connStr = _config.GetConnectionString("DefaultConnection")
                       ?? _db.Database.GetConnectionString();
            if (!string.IsNullOrWhiteSpace(connStr))
            {
                using var sqlConn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
                await sqlConn.OpenAsync();
                using var cmd = sqlConn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(1) FROM walletreq WHERE LTRIM(RTRIM(chqno)) = @utr";
                var p = cmd.CreateParameter();
                p.ParameterName = "@utr";
                p.Value = trimmed;
                cmd.Parameters.Add(p);

                var result = await cmd.ExecuteScalarAsync();
                var count = Convert.ToInt32(result ?? 0);
                if (count > 0)
                    return "This UTR / Transaction No. is already used. Please use a different one.";
            }
        }
        catch
        {
            // If the master table isn't reachable for any reason, fall through —
            // we still check our own Payments ledger below. Production should log this.
        }

        // 2. Our own Payments table
        var existsHere = await _db.Payments
            .AsNoTracking()
            .AnyAsync(p => p.UTRNumber == trimmed);
        if (existsHere)
            return "This UTR / Transaction No. is already used. Please use a different one.";

        return null;
    }

    /// <summary>
    /// AJAX: real-time UTR availability check (called from the form on blur).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> CheckUtr(string utr)
    {
        if (string.IsNullOrWhiteSpace(utr))
            return Json(new { available = true });

        var reason = await CheckUtrDuplicateAsync(utr.Trim());
        return Json(new
        {
            available = reason == null,
            message   = reason ?? "UTR is available."
        });
    }

    // GET: Upload Documents — DEPRECATED route, kept for backward compatibility.
    // All KYC/Property/GPS uploads have moved into the PM Surya Ghar page so
    // the user has a single place to manage everything. Redirect old callers.
    public async Task<IActionResult> UploadDocuments(int? id)
    {
        if (id is null or 0)
            return RedirectToAction(nameof(Index));

        // Verify the request belongs to this user before redirecting
        var result = await _solarRequestService.GetByIdAsync(id.Value);
        if (!result.IsSuccess) return NotFound();

        return RedirectToAction("Upload", "PMSurya", new { id = id.Value });
    }

    // POST: Upload Document (AJAX)
    [HttpPost]
    public async Task<IActionResult> UploadDocument(int requestId, string documentType, IFormFile file)
    {
        if (file == null)
            return Json(new { success = false, message = "No file provided" });

        var (success, filePath, error) = await _fileUploadService.UploadAsync(file, $"documents/{requestId}");
        if (!success)
            return Json(new { success = false, message = error });

        var userId = _userManager.GetUserId(User)!;
        await _documentService.SaveDocumentAsync(new SaveDocumentDto
        {
            SolarRequestId = requestId,
            UserId = userId,
            DocumentType = Enum.Parse<Domain.Enums.DocumentType>(documentType),
            FilePath = filePath!,
            FileName = Path.GetFileNameWithoutExtension(file.FileName),
            OriginalFileName = file.FileName,
            FileSizeBytes = file.Length,
            ContentType = file.ContentType
        });

        return Json(new { success = true, filePath, message = "Document uploaded successfully" });
    }

    // GET: My Projects list
    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.GetByUserIdAsync(userId);
        var projects = (result.Data ?? Enumerable.Empty<SolarRequestDto>()).ToList();

        // Compute per-project payment totals so the table can show
        // Total Amount / Total Paid / Remaining columns.
        // Sequential awaits — EF Core forbids concurrent ops on the same DbContext.
        var paidMap   = new Dictionary<int, decimal>();
        var imagesMap = new Dictionary<int, List<(string url, string label)>>();

        foreach (var p in projects)
        {
            paidMap[p.Id] = await _paymentService.GetTotalPaidAsync(p.Id);

            // Collect all images attached to this request — payment receipts,
            // site survey photos, KYC/PM docs — so the row can show a thumbnail
            // strip and clicking opens a lightbox.
            var imgs = new List<(string url, string label)>();

            // Payment receipts
            var payments = await _db.Payments
                                    .Where(x => x.SolarRequestId == p.Id)
                                    .ToListAsync();
            foreach (var pay in payments)
            {
                if (!string.IsNullOrWhiteSpace(pay.ReceiptImagePath) && IsImagePath(pay.ReceiptImagePath))
                {
                    imgs.Add((NormalizeUrl(pay.ReceiptImagePath), $"Payment Receipt — ₹{pay.Amount:N0}"));
                }
            }

            // Site Survey roof + GPS photos
            var surveys = await _db.SiteSurveys
                                   .Where(x => x.SolarRequestId == p.Id)
                                   .ToListAsync();
            foreach (var s in surveys)
            {
                if (!string.IsNullOrWhiteSpace(s.RoofPhotoPath) && IsImagePath(s.RoofPhotoPath))
                    imgs.Add((NormalizeUrl(s.RoofPhotoPath), "Roof Photo"));
                if (!string.IsNullOrWhiteSpace(s.GpsPhotoPath) && IsImagePath(s.GpsPhotoPath))
                    imgs.Add((NormalizeUrl(s.GpsPhotoPath), "GPS / Location Photo"));
            }

            // KYC / generic Documents
            var docs = await _db.Documents
                                .Where(x => x.SolarRequestId == p.Id)
                                .ToListAsync();
            foreach (var d in docs)
            {
                if (!string.IsNullOrWhiteSpace(d.FilePath) && IsImagePath(d.FilePath))
                    imgs.Add((NormalizeUrl(d.FilePath), d.DocumentType.ToString()));
            }

            imagesMap[p.Id] = imgs;
        }
        ViewBag.PaidMap   = paidMap;
        ViewBag.ImagesMap = imagesMap;
        return View(projects);
    }

    // --- helpers for the project image column ---
    private static bool IsImagePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        var lower = p.ToLowerInvariant();
        return lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") ||
               lower.EndsWith(".png") || lower.EndsWith(".gif") ||
               lower.EndsWith(".webp");
    }
    private static string NormalizeUrl(string p)
    {
        // Stored paths may be "uploads/x.jpg" or "/uploads/x.jpg" — normalize to a leading slash.
        var u = p.Replace('\\', '/').TrimStart('/');
        return "/" + u;
    }

    // GET: Project detail + status flow
    public async Task<IActionResult> Details(int id)
    {
        var result = await _solarRequestService.GetWithDetailsAsync(id);
        if (!result.IsSuccess) return NotFound();
        return View(result.Data);
    }

    // GET: Payment page
    public async Task<IActionResult> Payment(int id)
    {
        var result = await _solarRequestService.GetByIdAsync(id);
        if (!result.IsSuccess) return NotFound();
        ViewBag.Payments         = await _paymentService.GetByRequestIdAsync(id);
        ViewBag.TotalPaid        = await _paymentService.GetTotalPaidAsync(id);
        ViewBag.VerifiedPaid     = await _paymentService.GetVerifiedPaidAsync(id);
        ViewBag.MinimumThreshold = PaymentService.MinimumPaymentThreshold;
        ViewBag.HasMetMinimum    = await _paymentService.HasMetMinimumAsync(id);
        return View(result.Data);
    }

    // POST: Add Payment (AJAX)
    // Business rules enforced here:
    //   1. Amount must be > 0
    //   2. The FIRST payment must be ≥ ₹20,000 (minimum to start workflow)
    //   3. Cumulative payments cannot exceed the project's total amount
    //   4. Payment is saved as Pending — admin verification advances stage
    [HttpPost]
    public async Task<IActionResult> AddPayment(CreatePaymentDto dto, IFormFile? receiptImage)
    {
        var userId = _userManager.GetUserId(User)!;
        dto.UserId = userId;

        if (dto.Amount <= 0)
            return Json(new { success = false, message = "Amount must be greater than zero." });

        // UTR duplicate check — fail fast before any other validation so the
        // user sees the exact reason and doesn't lose the receipt upload to a
        // generic error.
        if (!string.IsNullOrWhiteSpace(dto.UTRNumber))
        {
            var dupReason = await CheckUtrDuplicateAsync(dto.UTRNumber.Trim());
            if (dupReason != null)
                return Json(new { success = false, message = dupReason });
        }

        // Look up the project to know its total amount
        var reqResult = await _solarRequestService.GetByIdAsync(dto.SolarRequestId);
        if (!reqResult.IsSuccess || reqResult.Data == null)
            return Json(new { success = false, message = "Solar request not found." });

        var projectTotal   = reqResult.Data.RequestedAmount;
        var alreadyPaid    = await _paymentService.GetTotalPaidAsync(dto.SolarRequestId);
        var min            = PaymentService.MinimumPaymentThreshold;

        // Effective minimum for the FIRST payment:
        //   - Normally ₹20,000.
        //   - But if the project itself is smaller than ₹20,000 (e.g. a ₹15,900 BV
        //     product), then the effective minimum is the project total itself —
        //     paying the full amount in one shot is acceptable, and the ₹20K floor
        //     should not block it.
        var effectiveMin = projectTotal > 0 && projectTotal < min ? projectTotal : min;

        // Rule: first payment must clear the effective minimum in one go
        if (alreadyPaid <= 0 && dto.Amount < effectiveMin)
            return Json(new
            {
                success = false,
                message = projectTotal > 0 && projectTotal < min
                    ? $"For this ₹{projectTotal:N0} project, the first payment must be the full ₹{projectTotal:N0}. You entered ₹{dto.Amount:N0}."
                    : $"First payment must be at least ₹{min:N0}. You entered ₹{dto.Amount:N0}."
            });

        // Rule: total cannot exceed the project amount
        if (alreadyPaid + dto.Amount > projectTotal)
        {
            var remaining = Math.Max(0, projectTotal - alreadyPaid);
            return Json(new
            {
                success = false,
                message = remaining > 0
                    ? $"This payment of ₹{dto.Amount:N0} would exceed your project total of ₹{projectTotal:N0}. You've already submitted ₹{alreadyPaid:N0} — maximum you can add now is ₹{remaining:N0}."
                    : $"Your submitted payments (₹{alreadyPaid:N0}) already match the project total of ₹{projectTotal:N0}. No further payment is needed."
            });
        }

        if (receiptImage != null)
        {
            var (success, path, error) = await _fileUploadService.UploadAsync(receiptImage, "payments");
            if (success) dto.ReceiptImagePath = path;
        }

        var result = await _paymentService.CreateAsync(dto);
        if (!result.IsSuccess)
            return Json(new { success = false, message = result.Message ?? result.Errors.FirstOrDefault() });

        // Show user the totals (unverified + verified) for transparency
        var totalSubmitted = await _paymentService.GetTotalPaidAsync(dto.SolarRequestId);
        var totalVerified  = await _paymentService.GetVerifiedPaidAsync(dto.SolarRequestId);

        var message = totalVerified >= min
            ? $"Payment submitted. Verified total ₹{totalVerified:N0} already meets the ₹{min:N0} minimum."
            : $"Payment submitted (₹{dto.Amount:N0}). Awaiting admin verification — workflow advances only after admin approves and verified total reaches ₹{min:N0}.";

        return Json(new
        {
            success = true,
            totalSubmitted = totalSubmitted,
            totalVerified = totalVerified,
            minimum = min,
            awaitingApproval = totalVerified < min,
            message = message
        });
    }

    // GET: Status tracker
    public async Task<IActionResult> Status(int? id)
    {
        var userId = _userManager.GetUserId(User)!;
        SolarRequestDto? data = null;

        if (id.HasValue)
        {
            var result = await _solarRequestService.GetWithDetailsAsync(id.Value);
            if (result.IsSuccess) data = result.Data;
        }
        if (data == null)
        {
            var projects = await _solarRequestService.GetByUserIdAsync(userId);
            data = projects.Data?.FirstOrDefault();
        }

        // Surface payment totals so the user can see why the workflow has/hasn't advanced
        if (data != null)
        {
            ViewBag.TotalSubmitted = await _paymentService.GetTotalPaidAsync(data.Id);
            ViewBag.VerifiedPaid   = await _paymentService.GetVerifiedPaidAsync(data.Id);
            ViewBag.Minimum        = PaymentService.MinimumPaymentThreshold;
        }
        return View(data);
    }

    // AJAX: Get status flow JSON
    [HttpGet]
    public async Task<IActionResult> GetStatusFlow(int id)
    {
        var result = await _solarRequestService.GetWithDetailsAsync(id);
        if (!result.IsSuccess)
            return Json(new { success = false });

        var project = result.Data!;
        return Json(new
        {
            success = true,
            currentStage = (int)project.CurrentStage,
            approvalStatus = project.ApprovalStatus.ToString(),
            requestNumber = project.RequestNumber
        });
    }
}
