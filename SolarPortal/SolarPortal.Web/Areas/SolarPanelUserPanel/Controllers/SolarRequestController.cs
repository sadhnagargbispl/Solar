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
    private readonly IBasicProductService _basicProducts;
    private readonly IPayModeService _payModes;
    private readonly IStateService _states;
    private readonly ILegacyProductRequestService _legacyBridge;
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
        IBasicProductService basicProducts,
        IPayModeService payModes,
        IStateService states,
        ILegacyProductRequestService legacyBridge,
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
        _basicProducts = basicProducts;
        _payModes = payModes;
        _states = states;
        _legacyBridge = legacyBridge;
        _userManager = userManager;
        _db = db;
        _config = config;
    }

    // GET: New Request Form (multi-step)
    public async Task<IActionResult> Create()
    {
        var userId = _userManager.GetUserId(User)!;
        var existing = await _solarRequestService.GetByUserIdAsync(userId);

        // === Profile auto-fill ===
        // Per spec: "Name & Address profile se auto-fill and readonly rakho."
        // Pull the logged-in user's profile + bridged member master (if available)
        // so the form has values out of the gate. ApplicationUser is the cheapest
        // source; MMemberMaster has fuller data when present.
        var meUser = await _userManager.GetUserAsync(User);
        var meMember = await _db.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.IdNo != null && m.IdNo.Trim() == userId);

        string profileName = !string.IsNullOrWhiteSpace(meUser?.FullName)
            ? meUser!.FullName!
            : (meMember != null ? meMember.FullName : (meUser?.UserName ?? ""));

        string profileAddress = meUser?.Address
                              ?? meMember?.Address1
                              ?? "";
        string profileCity = meUser?.City ?? meMember?.City ?? "";
        string profileState = meUser?.State ?? "";       // states stored as code on MMemberMaster — skip
        string profilePin = meUser?.PinCode ?? meMember?.PinCode ?? "";
        string profileMobile = meUser?.MobileNumber
                             ?? meMember?.Mobl?.ToString()
                             ?? "";
        // The Identity bridge (LiveDbAuthBridge) generates a synthetic
        // "member-{idNo}@livedb.local" email when a legacy m_membermaster user
        // first signs in — because ASP.NET Identity requires an email column.
        // That synthetic value should NEVER be shown to the user as if it
        // were their real email. If the only email we have is synthetic,
        // treat it as blank so the user can fill in (or leave) their real one.
        string rawEmail = meUser?.Email ?? meMember?.EMail ?? "";
        bool isSyntheticEmail = rawEmail.EndsWith("@livedb.local", StringComparison.OrdinalIgnoreCase);
        string profileEmail = isSyntheticEmail ? "" : rawEmail;

        // === Active-member gating ===
        // Per spec: if m_membermaster.ActiveStatus = 'Y', the user is an existing
        // active solar member. Such users CANNOT pick "With Activation" or
        // "Only Solar Without Activation" — those flows are for new/inactive
        // members. They can only file an "Already Active — Only Request".
        // The view uses this flag to disable the other two mode cards, and
        // the POST handler enforces it again (defense-in-depth).
        bool isActiveMember = meMember != null
                              && string.Equals(
                                  (meMember.ActiveStatus ?? "").Trim(),
                                  "Y",
                                  StringComparison.OrdinalIgnoreCase);
        ViewBag.IsActiveMember = isActiveMember;

        if (existing.IsSuccess && existing.Data != null && existing.Data.Any())
        {
            // STRICT LIFETIME RULE — one IdNo, one solar request, forever.
            // EXCEPTIONS where the user is allowed to (re)fill the form:
            //   (a) the auto-stub created at first login — meant to be filled in
            //   (b) a Rejected request — admin rejected it, user must be able to
            //       fix the issues admin flagged and re-submit. Per spec
            //       "user panel pe request reject ki to wapis laga sake request."
            //
            // Approved / In-progress / Completed states stay LOCKED.
            var latest = existing.Data
                .OrderByDescending(r => r.CreatedAt)
                .First();

            var isAutoStub = (latest.CurrentStage == ProjectStatus.Registration ||
                              latest.CurrentStage == ProjectStatus.ProductSelection) &&
                             latest.ApprovalStatus == ApprovalStatus.Pending &&
                             latest.SolarProjectId == null;

            // Rejected requests are re-fillable. We surface the rejection note
            // (if any) at the top of the form so the user knows what to fix.
            var isRejected = latest.ApprovalStatus == ApprovalStatus.Rejected;

            if (isAutoStub || isRejected)
            {
                // Allow the form to open so the user can pick a plan + fill / re-fill
                ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
                ViewBag.BasicProducts = await _basicProducts.GetActiveAsync();
                ViewBag.PayModes = await _payModes.GetActiveAsync();
                ViewBag.States = await _states.GetActiveAsync();

                if (isRejected)
                {
                    // Show the rejection reason at the top of the form so the user
                    // knows what admin flagged. RejectionReason is the dedicated
                    // field set by SolarRequestService.RejectAsync. AdminNotes is
                    // a fallback for older records that used the generic field.
                    var rejectMsg = !string.IsNullOrWhiteSpace(latest.RejectionReason)
                                        ? latest.RejectionReason
                                        : latest.AdminNotes;
                    ViewBag.RejectionNote = !string.IsNullOrWhiteSpace(rejectMsg)
                        ? rejectMsg
                        : "Your previous submission was rejected by admin. Please review and resubmit.";
                }

                // For Email: profileEmail is already pre-filtered (synthetic
                // emails were blanked out above). But the stub's stored Email
                // might also be a synthetic value from an older submission, so
                // we filter again as defense-in-depth.
                var stubEmail = latest.Email ?? string.Empty;
                if (stubEmail.EndsWith("@livedb.local", StringComparison.OrdinalIgnoreCase))
                    stubEmail = string.Empty;

                var prefill = new CreateSolarRequestViewModel
                {
                    // Name & Address come from profile (readonly in view) — fall back
                    // to the stub's stored values if the profile fields are blank.
                    ApplicantName = !string.IsNullOrWhiteSpace(profileName) ? profileName : (latest.ApplicantName ?? string.Empty),
                    MobileNumber = !string.IsNullOrWhiteSpace(profileMobile) ? profileMobile : (latest.MobileNumber ?? string.Empty),
                    Email = !string.IsNullOrWhiteSpace(profileEmail) ? profileEmail : stubEmail,
                    Address = !string.IsNullOrWhiteSpace(profileAddress) ? profileAddress : (latest.Address ?? string.Empty),
                    City = !string.IsNullOrWhiteSpace(profileCity) ? profileCity : (latest.City ?? string.Empty),
                    State = !string.IsNullOrWhiteSpace(profileState) ? profileState : (latest.State ?? string.Empty),
                    PinCode = !string.IsNullOrWhiteSpace(profilePin) ? profilePin : (latest.PinCode ?? string.Empty),
                    // Aadhaar & PAN are no longer collected on this form per spec —
                    // we preserve any prior value so the request keeps its data.
                    AadharNumber = latest.AadharNumber,
                    PANNumber = latest.PANNumber,
                    ConnectionType = latest.ConnectionType,
                    KVCapacity = latest.KVCapacity,
                    SolarProjectId = latest.SolarProjectId,
                    SelectedPlan = latest.SelectedPlan,
                    PlanAmount = latest.RequestedAmount,
                    // If the user is an active member (ActiveStatus=Y) we must lock
                    // them to "Already Active" mode regardless of what the request had.
                    // The view also disables the other two cards so they can't switch.
                    RequestType = isActiveMember
                                        ? RequestType.AlreadyActiveOnlyRequest
                                        : latest.RequestType
                };
                ViewBag.EditingRequestId = latest.Id;
                ViewBag.ProfileReadonly = true;
                return View(prefill);
            }

            // Approved / in-progress / completed → BLOCK, redirect to Status.
            string statusLabel = latest.ApprovalStatus switch
            {
                ApprovalStatus.Approved when latest.CurrentStage == ProjectStatus.Completed => "completed",
                ApprovalStatus.Approved => "approved and in progress",
                _ => "in progress"
            };

            TempData["Warning"] = $"You already have a solar request ({latest.RequestNumber}) which is {statusLabel}. " +
                                   "Only one solar request is allowed per user. Please track its status here.";
            return RedirectToAction(nameof(Status), new { id = latest.Id });
        }

        ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
        ViewBag.BasicProducts = await _basicProducts.GetActiveAsync();
        ViewBag.PayModes = await _payModes.GetActiveAsync();
        ViewBag.States = await _states.GetActiveAsync();
        ViewBag.ProfileReadonly = true;
        return View(new CreateSolarRequestViewModel
        {
            ApplicantName = profileName,
            MobileNumber = profileMobile,
            Email = profileEmail,
            Address = profileAddress,
            City = profileCity,
            State = profileState,
            PinCode = profilePin,
            // Active members in m_membermaster only have one valid mode: Already Active.
            // Default the form to that so they don't see a half-filled With Activation
            // form they aren't allowed to submit anyway.
            RequestType = isActiveMember
                                ? RequestType.AlreadyActiveOnlyRequest
                                : RequestType.WithActivation
        });
    }

    // POST: Step 1 - Personal Info
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateSolarRequestViewModel model)
    {
        // === Strip validation errors on fields the user CAN'T fix ===
        // Per spec: "userid login kar raha hai aur uska address/email/mobile/city/
        // state/district koi bhi blank ho, request fir bhi submit honi chahiye.
        // User edit nahi kar sakta." These fields are profile-fed and readonly on
        // the form — if any are blank or malformed in the source data, we don't
        // want ModelState validation blocking the submission since the user has
        // no way to correct them.
        //
        // Aadhaar / PAN are similar: their UI inputs were removed but hidden
        // values are round-tripped, and legacy data may not match the strict
        // format regex. Clear any such errors here so they never reach the user.
        //
        // We do this BEFORE checking ModelState.IsValid below.
        var fieldsToIgnoreErrors = new[]
        {
            nameof(CreateSolarRequestViewModel.ApplicantName),
            nameof(CreateSolarRequestViewModel.MobileNumber),
            nameof(CreateSolarRequestViewModel.AlternateMobile),
            nameof(CreateSolarRequestViewModel.Email),
            nameof(CreateSolarRequestViewModel.Address),
            nameof(CreateSolarRequestViewModel.City),
            nameof(CreateSolarRequestViewModel.State),
            nameof(CreateSolarRequestViewModel.PinCode),
            nameof(CreateSolarRequestViewModel.AadharNumber),
            nameof(CreateSolarRequestViewModel.PANNumber),
        };
        foreach (var key in fieldsToIgnoreErrors)
        {
            // ModelState's TryRemove only removes the entry — Errors collection
            // inside it gets cleared along with it. ASP.NET Core then treats the
            // field as untouched-but-valid.
            if (ModelState.ContainsKey(key))
                ModelState.Remove(key);
        }

        // Normalize PAN to uppercase if a value was provided (regex no longer
        // applies but legacy data may have it)
        if (!string.IsNullOrWhiteSpace(model.PANNumber))
            model.PANNumber = model.PANNumber.Trim().ToUpperInvariant();

        // Server-side enforcement.
        // Q1: Approved request — BLOCK forever.
        var userIdEarly = _userManager.GetUserId(User)!;

        // === Active-member POST guard ===
        // Defense-in-depth: even if the user manipulates the form via DevTools to
        // submit RequestType = WithActivation while they're an active member in
        // m_membermaster, we re-check ActiveStatus on the server and force-correct.
        var memberRow = await _db.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.IdNo != null && m.IdNo.Trim() == userIdEarly);
        bool isActiveMemberPost = memberRow != null
                                  && string.Equals(
                                      (memberRow.ActiveStatus ?? "").Trim(),
                                      "Y",
                                      StringComparison.OrdinalIgnoreCase);
        if (isActiveMemberPost && model.RequestType != RequestType.AlreadyActiveOnlyRequest)
        {
            // Hard-reject rather than silently coerce — the user explicitly picked a
            // mode they're not allowed to use, so they should know it's blocked.
            ModelState.AddModelError(nameof(model.RequestType),
                "Aap already an active solar member hain (ActiveStatus = Y). " +
                "Aap sirf \"Already Active — Only Request\" file kar sakte hain.");
            ViewBag.IsActiveMember = true;
            ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
            ViewBag.BasicProducts = await _basicProducts.GetActiveAsync();
            ViewBag.PayModes = await _payModes.GetActiveAsync();
            ViewBag.States = await _states.GetActiveAsync();
            ViewBag.ProfileReadonly = true;
            ViewBag.PreservedReceiptName = model.PaymentReceipt?.FileName;
            // Force the form to the only valid mode for next render
            model.RequestType = RequestType.AlreadyActiveOnlyRequest;
            return View(model);
        }

        var existingEarly = await _solarRequestService.GetByUserIdAsync(userIdEarly);
        SolarRequestDto? stubToUpdate = null;
        bool isResubmitOfRejected = false;
        if (existingEarly.IsSuccess && existingEarly.Data != null && existingEarly.Data.Any())
        {
            // STRICT LIFETIME RULE — same as GET. Auto-stub OR Rejected can be
            // (re)submitted. Anything else (Approved / in-progress / completed)
            // is locked.
            var latest = existingEarly.Data
                .OrderByDescending(r => r.CreatedAt)
                .First();

            var isAutoStub = (latest.CurrentStage == ProjectStatus.Registration ||
                              latest.CurrentStage == ProjectStatus.ProductSelection) &&
                             latest.ApprovalStatus == ApprovalStatus.Pending &&
                             latest.SolarProjectId == null;
            var isRejected = latest.ApprovalStatus == ApprovalStatus.Rejected;

            if (isAutoStub || isRejected)
            {
                stubToUpdate = latest;
                isResubmitOfRejected = isRejected;
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

        // ─── Payment method mandatory (per spec) ─────────────────────────
        if (model.PaymentAmount > 0 && string.IsNullOrWhiteSpace(model.PaymentMethod))
        {
            ModelState.AddModelError(nameof(model.PaymentMethod), "Payment Method is required.");
        }

        // ─── Payment receipt file mandatory (per spec) ───────────────────
        // Bina receipt file choose kiye request submit nahi honi chahiye. We
        // enforce this on the SERVER too (not just the client) so a request can
        // never be created without proof of payment, even if the JS is bypassed.
        if (model.PaymentReceipt == null || model.PaymentReceipt.Length == 0)
        {
            ModelState.AddModelError(nameof(model.PaymentReceipt),
                "Payment receipt (image / PDF) is required. Please upload it to submit.");
        }

        // ─── Basic product mandatory for With Activation (per spec) ──────
        if (model.RequestType == RequestType.WithActivation && !model.ExternalProductId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ExternalProductId),
                "Please pick one of the basic products.");
        }

        if (!ModelState.IsValid)
        {
            ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
            ViewBag.BasicProducts = await _basicProducts.GetActiveAsync();
            ViewBag.PayModes = await _payModes.GetActiveAsync();
            ViewBag.States = await _states.GetActiveAsync();
            ViewBag.ProfileReadonly = true;
            ViewBag.IsActiveMember = isActiveMemberPost;
            // Per spec: "Invalid email par validation do but uploaded receipt remove na ho."
            // IFormFile is not bound back from the original POST, so on re-render the
            // file input shows empty. We surface the original receipt's filename to the
            // user so they know it's still in-flight, and we DO NOT clear it from the
            // model (the receipt is uploaded only when the full POST succeeds — so they
            // need to re-select; we just keep the UX hint about the file).
            ViewBag.PreservedReceiptName = model.PaymentReceipt?.FileName;
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
                model.SelectedPlan = string.IsNullOrWhiteSpace(model.SelectedPlan)
                                        ? (basis.SelectedPlan ?? "Already Active — Only Request")
                                        : model.SelectedPlan;
                model.PlanAmount = basis.RequestedAmount;
                model.KVCapacity = basis.KVCapacity;
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
                    model.SelectedPlan = string.IsNullOrWhiteSpace(model.SelectedPlan)
                                            ? $"Already Active — {matched.Name}"
                                            : model.SelectedPlan;
                    model.PlanAmount = matched.ProjectAmount;
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
                model.SelectedPlan = $"Only Solar — {matched.Name}";
                model.PlanAmount = matched.ProjectAmount;
                model.KVCapacity = matched.SolarTypeKV;
                // Preserve the user's ConnectionType selection — don't override.
            }
            else
            {
                model.SolarProjectId = null;
                model.SelectedPlan = "Only Solar — Without Activation (pending plan assignment)";
                if (model.PlanAmount < 0) model.PlanAmount = 0m;
                if (model.KVCapacity <= 0) model.KVCapacity = 0m;
            }
        }
        // === With Activation + basic product picked ===
        // Per spec: in With Activation mode the user picks one of the basic products
        // surfaced from V#SpProductDetail. We RE-FETCH the row server-side so the
        // user can't tamper with the DP via the hidden form field. SolarProjectId
        // is intentionally left NULL — this isn't a SolarProjects master record.
        else if (model.RequestType == RequestType.WithActivation && model.ExternalProductId.HasValue)
        {
            var product = await _basicProducts.GetByIdAsync(model.ExternalProductId.Value);
            if (product == null)
            {
                ModelState.AddModelError(nameof(model.ExternalProductId),
                    "Selected product is not available. Please pick another.");
                ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
                ViewBag.BasicProducts = await _basicProducts.GetActiveAsync();
                ViewBag.PayModes = await _payModes.GetActiveAsync();
                ViewBag.States = await _states.GetActiveAsync();
                ViewBag.ProfileReadonly = true;
                ViewBag.PreservedReceiptName = model.PaymentReceipt?.FileName;
                return View(model);
            }
            if (product.Stock <= 0)
            {
                ModelState.AddModelError(nameof(model.ExternalProductId),
                    $"\"{product.ProductName}\" is out of stock. Please pick another product.");
                ViewBag.Projects = await _solarProjectService.GetAllAsync(activeOnly: true);
                ViewBag.BasicProducts = await _basicProducts.GetActiveAsync();
                ViewBag.PayModes = await _payModes.GetActiveAsync();
                ViewBag.States = await _states.GetActiveAsync();
                ViewBag.ProfileReadonly = true;
                ViewBag.PreservedReceiptName = model.PaymentReceipt?.FileName;
                return View(model);
            }

            model.SolarProjectId = null;                       // not a SolarProjects entry
            // Per spec: "With activation ho ya without ya already active — sabhi
            // mein project amount 1 kW / 2 kW (KV master plan) se hi hoga."
            // So the picked basic product is recorded ONLY as the legacy-bridge
            // product (ExternalProductId, already on the model). The project's
            // plan name + amount + capacity come from the KV master plan, exactly
            // like every other mode. We resolve that plan here by KVCapacity +
            // ConnectionType so PlanAmount is the kW amount, not the product DP.
            var actMatched = await FindMatchingPlanAsync(model.KVCapacity, model.ConnectionType);
            if (actMatched != null)
            {
                model.SolarProjectId = actMatched.Id;
                model.SelectedPlan = actMatched.Name;          // e.g. "2 kW Solar Kit"
                model.PlanAmount = actMatched.ProjectAmount; // e.g. ₹20,000
                model.KVCapacity = actMatched.SolarTypeKV;
            }
            else
            {
                // No KV plan matched — fall back to the product name for display
                // but leave PlanAmount to the safety-net / 0 so the user sees the
                // issue rather than being charged the wrong amount.
                model.SelectedPlan = product.ProductName;
            }
        }
        // If a SolarProject was picked (Mode 1 legacy), hydrate plan name + amount + kv from master
        else if (model.SolarProjectId.HasValue)
        {
            var project = await _solarProjectService.GetByIdAsync(model.SolarProjectId.Value);
            if (project != null)
            {
                model.SelectedPlan = project.Name;
                model.PlanAmount = project.ProjectAmount;
                model.KVCapacity = project.SolarTypeKV;
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
                model.SelectedPlan = matched.Name;
                model.PlanAmount = matched.ProjectAmount;
            }
        }

        // SAFETY NET: if PlanAmount is still 0 but we have a KVCapacity, do one last lookup.
        // This catches edge cases where Mode/RequestType branches missed assigning a plan.
        // Per the updated spec, the project amount ALWAYS comes from the KV master plan
        // (even in With Activation mode), so we no longer skip this for product picks —
        // the KV lookup is exactly what we want as the fallback.
        if (model.PlanAmount <= 0 && model.KVCapacity > 0)
        {
            var lastChance = await FindMatchingPlanAsync(model.KVCapacity, model.ConnectionType);
            if (lastChance != null)
            {
                model.SolarProjectId ??= lastChance.Id;
                model.PlanAmount = lastChance.ProjectAmount;
                if (string.IsNullOrWhiteSpace(model.SelectedPlan))
                    model.SelectedPlan = lastChance.Name;
            }
        }

        var userId = _userManager.GetUserId(User)!;
        // Null-coalesce profile strings to "" — see the long comment further down
        // where stubToUpdate is populated. Same rationale here: the DB columns
        // are NOT NULL with empty-string defaults, so we never pass through a
        // literal null even when the source profile is blank.
        var dto = new CreateSolarRequestDto
        {
            ApplicantName = model.ApplicantName ?? string.Empty,
            MobileNumber = model.MobileNumber ?? string.Empty,
            Email = model.Email ?? string.Empty,
            Address = model.Address ?? string.Empty,
            City = model.City ?? string.Empty,
            State = model.State ?? string.Empty,
            PinCode = model.PinCode ?? string.Empty,
            AadharNumber = model.AadharNumber,
            PANNumber = model.PANNumber,
            RequestType = model.RequestType,
            ConnectionType = model.ConnectionType,
            KVCapacity = model.KVCapacity,
            SolarProjectId = model.SolarProjectId,
            SelectedPlan = model.SelectedPlan,
            PlanAmount = model.PlanAmount,
            ExternalProductId = model.ExternalProductId
        };

        // If we're filling an auto-created stub, update it; else create new.
        ServiceResult<SolarRequestDto> result;
        if (stubToUpdate != null)
        {
            // Update the existing stub record with the form data
            var entity = await _db.SolarRequests.FirstOrDefaultAsync(r => r.Id == stubToUpdate.Id);
            if (entity != null)
            {
                // ─── Null-safety for NOT NULL string columns ───────────────
                // The DB schema declares Name/Address/City/State/PinCode/etc as
                // NOT NULL with a default empty string. The form may post these
                // as null (e.g. when the profile is blank and the user submits
                // a re-fill from a previously-rejected request whose underlying
                // m_membermaster row also has nulls). We coalesce to "" so the
                // database insert never sees a literal NULL — that's the SQL
                // error the user was seeing:
                //   "Cannot insert the value NULL into column 'Address'"
                entity.ApplicantName = dto.ApplicantName ?? string.Empty;
                entity.MobileNumber = dto.MobileNumber ?? string.Empty;
                entity.Email = dto.Email ?? string.Empty;
                entity.Address = dto.Address ?? string.Empty;
                entity.City = dto.City ?? string.Empty;
                entity.State = dto.State ?? string.Empty;
                entity.PinCode = dto.PinCode ?? string.Empty;
                // AadharNumber / PANNumber are nullable on the entity — null OK.
                entity.AadharNumber = dto.AadharNumber;
                entity.PANNumber = dto.PANNumber;
                entity.RequestType = dto.RequestType;
                entity.ConnectionType = dto.ConnectionType;
                entity.KVCapacity = dto.KVCapacity;
                entity.SolarProjectId = dto.SolarProjectId;
                entity.SelectedPlan = dto.SelectedPlan;
                entity.PlanAmount = dto.PlanAmount;
                entity.ExternalProductId = dto.ExternalProductId;
                entity.CurrentStage = ProjectStatus.Payment;  // ← advance to Payment stage

                // If this is a re-submit of a previously REJECTED request, reset
                // approval back to Pending so admin can review the new version.
                // Per spec: "user panel pe request reject ki to wapis laga sake."
                // We also archive the prior rejection note to the request history
                // (Notes field) so the audit trail is preserved.
                if (isResubmitOfRejected)
                {
                    // Entity field is AdminNotes (DTO maps it to .Notes). We append
                    // a re-submit marker so the audit trail is preserved.
                    var priorNote = entity.AdminNotes;
                    entity.ApprovalStatus = ApprovalStatus.Pending;
                    entity.AdminNotes = string.IsNullOrWhiteSpace(priorNote)
                        ? $"[Resubmitted on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC]"
                        : $"[Resubmitted on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC] | Prior reject reason: {priorNote}";
                }

                entity.UpdatedAt = DateTime.UtcNow;
                entity.UpdatedBy = userId;
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
            ViewBag.BasicProducts = await _basicProducts.GetActiveAsync();
            ViewBag.PayModes = await _payModes.GetActiveAsync();
            ViewBag.States = await _states.GetActiveAsync();
            ViewBag.ProfileReadonly = true;
            ViewBag.IsActiveMember = isActiveMemberPost;
            ViewBag.PreservedReceiptName = model.PaymentReceipt?.FileName;
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
                var hardMin = PaymentService.MinimumPaymentThreshold;
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
                        // Organise uploads by project: uploads/SCR-001/payments/<file>
                        var (ok, path, _) = await _fileUploadService.UploadAsync(
                            model.PaymentReceipt, $"{result.Data.RequestNumber}/payments");
                        if (ok) receiptPath = path;
                    }

                    var payDto = new CreatePaymentDto
                    {
                        SolarRequestId = result.Data.Id,
                        UserId = userId,
                        Amount = model.PaymentAmount,
                        UTRNumber = (model.PaymentUTR ?? "").Trim(),
                        PaymentDate = model.PaymentDate ?? DateTime.UtcNow,
                        PaymentMethod = string.IsNullOrWhiteSpace(model.PaymentMethod) ? "Online" : model.PaymentMethod,
                        ReceiptImagePath = receiptPath
                    };
                    var payResult = await _paymentService.CreateAsync(payDto);
                    if (!payResult.IsSuccess)
                    {
                        TempData["Warning"] = "Request saved, but the first payment couldn't be recorded: " +
                                              (payResult.Message ?? payResult.Errors.FirstOrDefault() ?? "unknown error");
                    }

                    // ─── Legacy SolFit bridge (per spec) ─────────────────────────
                    // For "With Activation" submissions we ALSO insert a row into the
                    // legacy ASP.NET (VB) table TrnProductorderDetail so the existing
                    // SolFit activation workflow (reports, dispatch queues, commission
                    // calc) continues to process this order — exactly like the old
                    // ProductWalletRequest.aspx page (tp=A) used to do.
                    //
                    // Failure here is non-fatal: the new SolarRequest record stays as
                    // the source of truth. Any error is surfaced as a warning so admin
                    // can reconcile in the legacy app.
                    if (model.RequestType == RequestType.WithActivation
                        && model.ExternalProductId.HasValue
                        && payResult.IsSuccess)
                    {
                        try
                        {
                            var idNo = _userManager.GetUserName(User) ?? userId; // ApplicationUser.Id = legacy IdNo
                            var bridgeResult = await _legacyBridge.InsertWithActivationAsync(new LegacyProductRequestInput
                            {
                                MemberIdNo = idNo,
                                ProductId = model.ExternalProductId.Value,
                                Qty = 1,
                                TxnId = (model.PaymentUTR ?? "").Trim(),
                                TxnDate = model.PaymentDate ?? DateTime.UtcNow,
                                PayModeId = MapPayMethodToLegacyPid(model.PaymentMethod),
                                ImageFileName = !string.IsNullOrWhiteSpace(receiptPath)
                                                ? System.IO.Path.GetFileName(receiptPath)
                                                : null,
                                Address = model.Address,
                                City = model.City,
                                District = model.City,    // legacy schema has District; we don't collect it separately
                                PinCode = model.PinCode,
                                StateName = model.State,
                                StateCode = model.State    // closest we have without a state code lookup
                            });
                            if (!bridgeResult.Success)
                            {
                                TempData["Warning"] = "Request saved, but legacy product-order sync failed: "
                                                      + (bridgeResult.ErrorMessage ?? "unknown error")
                                                      + ". Admin will reconcile.";
                            }
                        }
                        catch (Exception bridgeEx)
                        {
                            TempData["Warning"] = "Request saved, but legacy product-order sync threw: " + bridgeEx.Message;
                        }
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

    // Map a free-text payment method (the new app's PaymentMethod string —
    // "UPI", "NetBanking", "Card", "Cash") to the legacy M_PayModeMaster PID.
    // PID 1 = UPI in legacy SolFit; we default to that for anything we can't
    // recognise so the legacy insert always has a valid foreign key.
    private static int MapPayMethodToLegacyPid(string? method)
    {
        if (string.IsNullOrWhiteSpace(method)) return 1;
        var m = method.Trim().ToLowerInvariant();
        return m switch
        {
            "upi" => 1,
            "netbanking" or "online" => 2,
            "card" => 4,
            "cash" => 3,
            _ => 1
        };
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
            message = reason ?? "UTR is available."
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

        // Organise uploads by project: uploads/SCR-001/documents/<file>
        var reqForDoc = await _solarRequestService.GetByIdAsync(requestId);
        var docFolder = reqForDoc.IsSuccess && reqForDoc.Data != null
            ? $"{reqForDoc.Data.RequestNumber}/documents"
            : $"documents/{requestId}"; // fallback if lookup fails
        var (success, filePath, error) = await _fileUploadService.UploadAsync(file, docFolder);
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
        // Per spec: unfilled auto-stub ko list mein mat dikhao — jab tak user
        // actually request submit na kare, My Projects khali rahe.
        var projects = (result.Data ?? Enumerable.Empty<SolarRequestDto>())
                        .Where(p => !IsUnfilledStub(p))
                        .ToList();

        // Compute per-project payment totals so the table can show
        // Total Amount / Total Paid / Remaining columns.
        // Sequential awaits — EF Core forbids concurrent ops on the same DbContext.
        var paidMap = new Dictionary<int, decimal>();
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
        ViewBag.PaidMap = paidMap;
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

    // Shared stub detector — keep in sync with DashboardService.IsUnfilledStub.
    // A request is an "unfilled auto-stub" (created at first login, never touched)
    // when the user hasn't picked a plan/product, set a capacity, or entered an
    // amount. We deliberately key off the USER-FACING emptiness (no plan, no
    // product, KV 0, amount 0) rather than the exact stage/approval values —
    // those can drift (e.g. stage Payment vs ProductSelection) yet the row is
    // still an empty placeholder the user never filled. Per spec: jab tak user
    // request actually submit na kare, yeh stub kahin (Dashboard / My Solar
    // Status / My Projects) nahi dikhna chahiye.
    private static bool IsUnfilledStub(SolarRequestDto r) =>
        !r.SolarProjectId.HasValue &&
        !r.ExternalProductId.HasValue &&
        r.KVCapacity == 0m &&
        r.PlanAmount == 0m &&
        r.RequestedAmount == 0m &&
        r.CurrentStage != ProjectStatus.Completed; // a completed ₹0 project is still real

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

        // Per spec: agar yeh sirf auto-stub hai (user ne abhi request submit nahi
        // ki), to Details mein khaali data dikhane ke bajaye New Request page pe
        // bhej do — "no record" jaisa behaviour.
        if (result.Data != null && IsUnfilledStub(result.Data))
            return RedirectToAction(nameof(Create));

        return View(result.Data);
    }

    // GET: Payment page
    public async Task<IActionResult> Payment(int id)
    {
        var result = await _solarRequestService.GetByIdAsync(id);
        if (!result.IsSuccess) return NotFound();
        ViewBag.Payments = await _paymentService.GetByRequestIdAsync(id);
        ViewBag.TotalPaid = await _paymentService.GetTotalPaidAsync(id);
        ViewBag.VerifiedPaid = await _paymentService.GetVerifiedPaidAsync(id);
        ViewBag.MinimumThreshold = PaymentService.MinimumPaymentThreshold;
        ViewBag.HasMetMinimum = await _paymentService.HasMetMinimumAsync(id);
        // Payment methods come from the legacy M_PayModeMaster table so the
        // dropdown matches the old SolFit system exactly (per spec).
        ViewBag.PayModes = await _payModes.GetActiveAsync();
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

        // Payment method mandatory (per spec)
        if (string.IsNullOrWhiteSpace(dto.PaymentMethod))
            return Json(new { success = false, message = "Payment Method is required." });

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

        var projectTotal = reqResult.Data.RequestedAmount;
        var alreadyPaid = await _paymentService.GetTotalPaidAsync(dto.SolarRequestId);
        var min = PaymentService.MinimumPaymentThreshold;

        // Guard: if the project is already fully paid, reject any further payment
        // outright with a clear message. The UI hides the form in this case, but a
        // direct POST (or a stale open tab) could still reach here — so we enforce
        // it server-side too.
        if (projectTotal > 0 && alreadyPaid >= projectTotal)
            return Json(new
            {
                success = false,
                message = $"This project is already fully paid (₹{projectTotal:N0}). No further payment is needed."
            });

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
            // Organise uploads by project: uploads/SCR-001/payments/<file>
            var (success, path, error) = await _fileUploadService.UploadAsync(
                receiptImage, $"{reqResult.Data.RequestNumber}/payments");
            if (success) dto.ReceiptImagePath = path;
        }

        var result = await _paymentService.CreateAsync(dto);
        if (!result.IsSuccess)
            return Json(new { success = false, message = result.Message ?? result.Errors.FirstOrDefault() });

        // Show user the totals (unverified + verified) for transparency
        var totalSubmitted = await _paymentService.GetTotalPaidAsync(dto.SolarRequestId);
        var totalVerified = await _paymentService.GetVerifiedPaidAsync(dto.SolarRequestId);

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
            // Show the user's project so the timeline (Registration done, rest
            // pending) renders. Even an unfilled stub is shown here because the
            // user HAS registered — only the plan/amount details are hidden in
            // the view until they actually pick a plan.
            var list = projects.Data ?? new List<SolarRequestDto>();
            data = list.FirstOrDefault();
        }

        // Surface payment totals so the user can see why the workflow has/hasn't advanced
        if (data != null)
        {
            ViewBag.TotalSubmitted = await _paymentService.GetTotalPaidAsync(data.Id);
            ViewBag.VerifiedPaid = await _paymentService.GetVerifiedPaidAsync(data.Id);
            ViewBag.Minimum = PaymentService.MinimumPaymentThreshold;
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
