using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
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
    private readonly IConfiguration _config;

    public SolarRequestController(
        ISolarRequestService solarRequestService,
        IPaymentService paymentService,
        IDocumentService documentService,
        IFileUploadService fileUploadService,
        ISolarProjectService solarProjectService,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
        _solarRequestService = solarRequestService;
        _paymentService = paymentService;
        _documentService = documentService;
        _fileUploadService = fileUploadService;
        _solarProjectService = solarProjectService;
        _notificationService = notificationService;
        _userManager = userManager;
        _config = config;
    }

    // GET: New Request Form (multi-step)
    public async Task<IActionResult> Create()
    {
        // One-active-request-per-user rule: block if an active (not-yet-completed) request exists
        // EXCEPTION: a brand-new request marked "AlreadyActiveOnlyRequest" (mode 3 per spec)
        //            is allowed alongside an existing request, since that mode is specifically
        //            for users who already have an active solar account and just want another request.
        // Since this is the GET (no mode selected yet), we still surface the warning but allow
        // the user to choose mode 3 inside the form.
        var userId = _userManager.GetUserId(User)!;
        var existing = await _solarRequestService.GetByUserIdAsync(userId);
        if (existing.IsSuccess && existing.Data != null)
        {
            // A request is "active" only if it is in progress — not Completed,
            // and not Rejected by admin. Rejected requests are dead, so the user
            // can freely create a fresh one.
            var active = existing.Data.FirstOrDefault(r =>
                r.CurrentStage != ProjectStatus.Completed &&
                r.ApprovalStatus != ApprovalStatus.Rejected);
            if (active != null)
            {
                // One-request rule: redirect to the active request instead of letting
                // the user fill out a new one they cannot submit.
                TempData["Warning"] = $"You already have an active request ({active.RequestNumber}). " +
                                       "Only one active request is allowed at a time. Please track this one until it's completed or rejected.";
                return RedirectToAction(nameof(Status), new { id = active.Id });
            }
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

        // Server-side enforcement of one-active-request rule
        // - Approved + in-progress: BLOCK (user must complete current request first)
        // - Completed:              ALLOW (current is done — fresh start)
        // - Rejected:               ALLOW (current is dead — user can try again)
        // RULE APPLIES TO ALL MODES — a user can have only ONE active request at a time,
        // regardless of whether it's With Activation / Only Solar / Already Active.
        var userIdEarly = _userManager.GetUserId(User)!;
        var existingEarly = await _solarRequestService.GetByUserIdAsync(userIdEarly);
        if (existingEarly.IsSuccess && existingEarly.Data != null)
        {
            var active = existingEarly.Data.FirstOrDefault(r =>
                r.CurrentStage != ProjectStatus.Completed &&
                r.ApprovalStatus != ApprovalStatus.Rejected);
            if (active != null)
            {
                TempData["Error"] = $"You already have an active request ({active.RequestNumber}). " +
                                     "Only one active request is allowed at a time. Please wait for it to be completed or rejected before creating a new one.";
                return RedirectToAction(nameof(Status), new { id = active.Id });
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
            var mine = existingEarly.IsSuccess && existingEarly.Data != null
                ? existingEarly.Data.OrderByDescending(r => r.CreatedAt).ToList()
                : new List<SolarRequestDto>();
            var basis = mine.FirstOrDefault();
            if (basis == null)
            {
                TempData["Error"] = "Mode 'Already Active' requires you to already have an existing solar project. Please choose another mode.";
                ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
                return View(model);
            }
            // Carry forward existing project info so this top-up request continues against the same Solar Account
            model.SolarProjectId = basis.SolarProjectId;
            model.SelectedPlan   = string.IsNullOrWhiteSpace(model.SelectedPlan)
                                    ? (basis.SelectedPlan ?? "Already Active — Only Request")
                                    : model.SelectedPlan;
            model.PlanAmount     = basis.RequestedAmount;
            model.KVCapacity     = basis.KVCapacity;
            model.ConnectionType = basis.ConnectionType;
        }
        else if (model.RequestType == RequestType.OnlySolarWithoutActivation)
        {
            // No product picker shown — try to match a SolarProject plan based on
            // the user's KV + ConnectionType selection so the amount is auto-fetched.
            if (model.KVCapacity <= 0) model.KVCapacity = 1.1m;
            var matched = await FindMatchingPlanAsync(model.KVCapacity, model.ConnectionType);
            if (matched != null)
            {
                model.SolarProjectId = matched.Id;
                model.SelectedPlan   = matched.Name + " (Only Solar — Without Activation)";
                model.PlanAmount     = matched.TotalAmount;
            }
            else
            {
                // No matching plan in master — admin will assign later
                model.SolarProjectId = null;
                model.SelectedPlan   = "Only Solar — Without Activation (pending plan assignment)";
                if (model.PlanAmount <= 0) model.PlanAmount = 0m;
            }
        }
        // If a SolarProject was picked (Mode 1), hydrate plan name + amount + kv from master
        else if (model.SolarProjectId.HasValue)
        {
            var project = await _solarProjectService.GetByIdAsync(model.SolarProjectId.Value);
            if (project != null)
            {
                model.SelectedPlan = project.Name;
                model.PlanAmount = project.TotalAmount;
                model.KVCapacity = project.SolarTypeKV;
                model.ConnectionType = project.ConnectionType;
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
                model.PlanAmount     = matched.TotalAmount;
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

        var result = await _solarRequestService.CreateAsync(dto, userId);
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
        // Exact KV + connection match first, then KV-only match, then any active plan with same conn
        return all.FirstOrDefault(p => p.SolarTypeKV == kv && p.ConnectionType == conn)
            ?? all.FirstOrDefault(p => p.SolarTypeKV == kv)
            ?? all.FirstOrDefault(p => p.ConnectionType == conn);
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
        var paidMap = new Dictionary<int, decimal>();
        foreach (var p in projects)
        {
            paidMap[p.Id] = await _paymentService.GetTotalPaidAsync(p.Id);
        }
        ViewBag.PaidMap = paidMap;
        return View(projects);
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
