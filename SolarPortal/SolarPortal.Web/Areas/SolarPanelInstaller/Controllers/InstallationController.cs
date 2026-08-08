using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.Areas.SolarPanelInstaller.Controllers;

/// <summary>
/// "Mark Installation" ka asli kaam yahan hota hai (spec: admin panel par sirf
/// remark + assigned installer ka check rehta hai).
///
/// Kaam kaise banta hai: admin Material Dispatch ke time despatch person assign
/// karta hai. Wahi worker is panel ka installer hai. Admin agar remark save karta
/// hai to ek Installation row pehle se ban jati hai (IsCompleted = false); warna
/// row yahin banti hai jab installer complete karta hai.
///
/// Isliye queue do jagah se banti hai:
///   1. Installation rows jinka AssignedWorkerId = ye worker (admin ne remark daala)
///   2. MaterialDispatch rows jinka AssignedWorkerId = ye worker (koi row nahi bani)
/// </summary>
[Area("SolarPanelInstaller")]
[Authorize(Roles = "Installer")]
public class InstallationController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ISolarRequestService _requestService;
    private readonly IFileUploadService _fileUpload;
    private readonly IIncWalletService _incWallet;

    public InstallationController(IUnitOfWork uow, ISolarRequestService requestService,
        IFileUploadService fileUpload, IIncWalletService incWallet)
    {
        _uow = uow;
        _requestService = requestService;
        _fileUpload = fileUpload;
        _incWallet = incWallet;
    }

    /// <summary>
    /// Whether this INC's KYC is still outstanding.
    ///
    /// Installation does NOT require KYC - an installer can mark work complete
    /// either way. What KYC gates is the WITHDRAWAL of the money it earns, so this
    /// is only used to warn on the queue page; the hard stop lives in
    /// WithdrawController.
    ///
    /// All THREE sections must be Approved - Address Proof, Bank Detail and PAN.
    /// Part-approved is not approved: the bank section is what the commission is
    /// eventually paid against, so letting the work be completed on an unverified
    /// account only defers the problem to payout time.
    ///
    /// JOB workers are untouched - they are salaried and are never asked for KYC.
    /// Returns the message to show, or null when the installer may proceed.
    /// </summary>
    private async Task<string?> KycBlockAsync(int workerId)
    {
        var worker = await _uow.Workers.GetByIdAsync(workerId);

        // Read the type from the DB, not from the auth cookie: the cookie is
        // stamped at login and goes stale the moment admin switches a worker's
        // type, and this decides whether the rule applies at all.
        if (worker == null || worker.Type != WorkerType.INC) return null;

        var kyc = (await _uow.IncKycDocuments.FindAsync(k => k.WorkerId == workerId))
                  .OrderByDescending(k => k.Id)
                  .FirstOrDefault();

        if (kyc == null)
            return "You have not submitted your KYC yet. Commission you earn will be held " +
                   "until it is approved - you will not be able to withdraw.";

        if (kyc.IsFullyApproved) return null;

        // Name the sections that are actually holding it up, so the installer knows
        // what to fix instead of guessing.
        var pending = new List<string>();
        if (kyc.AddressStatus != ApprovalStatus.Approved) pending.Add($"Address Proof ({kyc.AddressStatus})");
        if (kyc.BankStatus != ApprovalStatus.Approved) pending.Add($"Bank Detail ({kyc.BankStatus})");
        if (kyc.PanStatus != ApprovalStatus.Approved) pending.Add($"PAN Card ({kyc.PanStatus})");

        return "Your KYC is not fully approved yet - " + string.Join(", ", pending) +
               ". Withdrawals stay blocked until all three sections are approved.";
    }

    private int WorkerId => int.TryParse(User.FindFirst("WorkerId")?.Value, out var id) ? id : 0;

    // GET: /SolarPanelInstaller/Installation
    // ?filter=pending | done | all   (default all, same as the admin reports)
    public async Task<IActionResult> Index(string? filter)
    {
        var wid = WorkerId;
        var f = (filter ?? "all").ToLowerInvariant();
        ViewBag.Filter = f;

        if (wid <= 0) return View(new List<InstallationRow>());

        // Image point 11: admin approves in their own app (shared DB); this is where
        // the approved installations actually get paid out. Idempotent, so running it
        // on every page load is safe. Never let a wallet issue break the queue.
        try
        {
            var creditMsg = await CreditApprovedInstallationsAsync(wid);
            if (!string.IsNullOrWhiteSpace(creditMsg)) TempData["Success"] = creditMsg;
        }
        catch (Exception ex)
        {
            TempData["Warning"] = $"Commission sweep failed: {ex.InnerException?.Message ?? ex.Message}";
        }

        // Every request this worker is attached to, from either source.
        var myInstalls = (await _uow.Installations.FindAsync(i => i.AssignedWorkerId == wid))
                         .GroupBy(i => i.SolarRequestId)
                         .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First());

        var myDispatches = (await _uow.MaterialDispatches.FindAsync(m => m.AssignedWorkerId == wid))
                           .GroupBy(m => m.SolarRequestId)
                           .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());

        var requestIds = myInstalls.Keys.Union(myDispatches.Keys).ToHashSet();
        if (requestIds.Count == 0) return View(new List<InstallationRow>());

        var requests = (await _uow.SolarRequests.FindAsync(r => requestIds.Contains(r.Id)))
                       .ToDictionary(r => r.Id);

        // Photo set per installation (image point 11) — one query for the whole page.
        var installIds = myInstalls.Values.Select(i => i.Id).ToHashSet();
        var photosByInstall = installIds.Count == 0
            ? new Dictionary<int, List<InstallationPhoto>>()
            : (await _uow.InstallationPhotos.FindAsync(p => installIds.Contains(p.InstallationId)))
              .GroupBy(p => p.InstallationId)
              .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).ToList());

        var allRows = requestIds
            .Where(requests.ContainsKey)
            .Select(id =>
            {
                myInstalls.TryGetValue(id, out var inst);
                myDispatches.TryGetValue(id, out var disp);
                return new InstallationRow
                {
                    Request = requests[id],
                    Installation = inst,
                    Dispatch = disp,
                    Photos = inst != null && photosByInstall.TryGetValue(inst.Id, out var ph)
                                ? ph
                                : new List<InstallationPhoto>()
                };
            })
            .OrderByDescending(row => row.Request.CreatedAt)
            .ToList();

        // Counts come from the UNFILTERED set so the tab badges stay honest no
        // matter which tab is open.
        ViewBag.PendingCount = allRows.Count(r => !r.IsCompleted && r.Request.CurrentStage == ProjectStatus.Installation);
        ViewBag.RejectedCount = allRows.Count(r => r.IsRejected);
        ViewBag.MaxPhotos = InstallationPhoto.MaxPerInstallation;

        // Informational only - installation is never blocked by KYC. It warns that
        // the money earned here cannot be withdrawn until KYC is approved, which is
        // better learned now than at withdrawal time.
        ViewBag.KycBlock = await KycBlockAsync(wid);

        // Actionable = project abhi Installation stage par hai aur complete nahi hua.
        // "rejected" is a separate bucket: admin sent the photos back and the
        // installer has to re-upload (image point 11).
        var rows = allRows
            .Where(row => f switch
            {
                "pending" => !row.IsCompleted && row.Request.CurrentStage == ProjectStatus.Installation,
                "rejected" => row.IsRejected,
                "done" => row.IsCompleted,
                _ => true
            })
            .ToList();
        return View(rows);
    }

    // POST: /SolarPanelInstaller/Installation/MarkComplete
    // Mirrors the admin flow that used to live in OperationsController.SubmitInstallation:
    // completes the Installation row, logs the WorkerAssignment and advances the stage
    // (Domestic -> DCR Update, Commercial -> Completed).
    //
    // Image point 11: the installer now attaches MULTIPLE photos (up to 30) and the
    // installation goes to admin as Pending. Commission is credited only after admin
    // approves; a rejected installation is re-uploaded through Resubmit below.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkComplete(int requestId, DateTime? installationDate,
        string? notes, string? remark, IFormFile? completionPhoto, List<IFormFile>? completionPhotos)
    {
        var wid = WorkerId;
        if (wid <= 0)
        {
            TempData["Warning"] = "Worker session not found. Please log in again.";
            return RedirectToAction(nameof(Index));
        }


        var request = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (request == null)
        {
            TempData["Warning"] = "Request not found.";
            return RedirectToAction(nameof(Index));
        }

        var installation = (await _uow.Installations.FindAsync(i => i.SolarRequestId == requestId))
                           .OrderByDescending(i => i.CreatedAt)
                           .FirstOrDefault();
        var dispatchWorkerId = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == requestId))
                               .OrderByDescending(m => m.CreatedAt)
                               .FirstOrDefault()?.AssignedWorkerId;

        // Only the assigned installer may complete this one.
        var ownerId = installation?.AssignedWorkerId ?? dispatchWorkerId;
        if (ownerId != wid)
        {
            TempData["Warning"] = "This installation is assigned to another installer.";
            return RedirectToAction(nameof(Index));
        }

        if (installation?.IsCompleted == true)
        {
            TempData["Warning"] = "This installation is already marked complete.";
            return RedirectToAction(nameof(Index));
        }

        // Image point 11: "Multiple photo upload — upto 30 photo."
        // Both field names are accepted so an older cached page posting the single
        // `completionPhoto` still works; everything lands in one list.
        var incoming = BuildPhotoList(completionPhoto, completionPhotos);
        if (incoming.Count > InstallationPhoto.MaxPerInstallation)
        {
            TempData["Warning"] = $"You selected {incoming.Count} photos — a maximum of " +
                                  $"{InstallationPhoto.MaxPerInstallation} is allowed.";
            return RedirectToAction(nameof(Index));
        }
        if (incoming.Count == 0)
        {
            TempData["Warning"] = "Please attach at least one installation photo before marking it complete.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var isNew = installation == null;
            installation ??= new Installation { SolarRequestId = requestId };

            installation.AssignedWorkerId = wid;
            installation.InstallationDate = installationDate ?? DateTime.UtcNow;
            installation.Notes = notes;
            // Admin ka remark tabhi overwrite karo jab installer ne apna likha ho.
            if (!string.IsNullOrWhiteSpace(remark)) installation.Remark = remark;
            installation.IsCompleted = true;
            installation.CompletedAt = DateTime.UtcNow;

            // Goes to admin for verification — commission waits for that approval.
            installation.ApprovalStatus = ApprovalStatus.Pending;
            installation.RejectionReason = null;
            installation.SubmittedAt = DateTime.UtcNow;

            if (isNew) await _uow.Installations.AddAsync(installation);
            else _uow.Installations.Update(installation);

            // Save first so a new row has its Id before the FK reference below.
            await _uow.SaveChangesAsync();

            // Photos need the Installation.Id, so they are stored right after.
            var savedPaths = await SavePhotosAsync(installation, requestId, wid, incoming);
            if (savedPaths.Count == 0)
            {
                TempData["Warning"] = "Installation photos could not be uploaded. Please try again.";
                return RedirectToAction(nameof(Index));
            }
            // Keep the legacy single-photo column pointing at the first photo so
            // every existing screen that reads it keeps rendering something.
            installation.CompletionPhotoPath = savedPaths[0];
            _uow.Installations.Update(installation);
            await _uow.SaveChangesAsync();

            var assignment = (await _uow.WorkerAssignments.FindAsync(a => a.InstallationId == installation.Id))
                             .OrderByDescending(a => a.Id)
                             .FirstOrDefault();
            if (assignment == null)
            {
                await _uow.WorkerAssignments.AddAsync(new WorkerAssignment
                {
                    InstallationId = installation.Id,
                    WorkerId = wid,
                    AssignedByUserId = "worker-" + wid,
                    AssignedDate = DateTime.UtcNow,
                    Notes = notes
                });
            }
            else
            {
                assignment.WorkerId = wid;
                _uow.WorkerAssignments.Update(assignment);
            }
            await _uow.SaveChangesAsync();

            var nextStage = request.ConnectionType == ConnectionType.Domestic
                ? ProjectStatus.DCRUpdate
                : ProjectStatus.Completed;

            var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
            {
                Id = requestId,
                NewStage = nextStage,
                Notes = $"Installation completed by installer on {installation.InstallationDate:dd/MM/yyyy}"
            }, "worker-" + wid);

            // Commission is NO LONGER credited here.
            //
            // Image point 11: "Admin ko approve hone par credit hona chahiye.
            // Reject hone par INC wala wapas update karega." So marking complete
            // only submits the photos for review; the money moves in
            // CreditApprovedInstallationsAsync once admin approves.
            var commissionMsg = " Photos submitted to admin — your commission is credited once admin approves.";

            TempData["Success"] = (stageResult.IsSuccess
                ? (nextStage == ProjectStatus.DCRUpdate
                    ? "Installation marked complete. DCR pending."
                    : "Installation marked complete. Project completed.")
                : $"Installation saved, but stage update failed: {stageResult.Message}") + commissionMsg;
        }
        catch (Exception ex)
        {
            TempData["Warning"] = $"Installation failed: {ex.InnerException?.Message ?? ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    // POST: /SolarPanelInstaller/Installation/Resubmit
    // Image point 11: "Reject hone par INC wala wapas update karega."
    // Admin rejected the photos — the installer attaches a fresh set, which clears
    // the reject reason and puts the installation back in front of admin.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resubmit(int installationId, string? notes,
        List<IFormFile>? completionPhotos)
    {
        var wid = WorkerId;
        if (wid <= 0)
        {
            TempData["Warning"] = "Worker session not found. Please log in again.";
            return RedirectToAction(nameof(Index));
        }

        var installation = await _uow.Installations.GetByIdAsync(installationId);
        if (installation == null || installation.AssignedWorkerId != wid)
        {
            TempData["Warning"] = "This installation is not assigned to you.";
            return RedirectToAction(nameof(Index));
        }
        if (installation.ApprovalStatus != ApprovalStatus.Rejected)
        {
            TempData["Warning"] = "Only a rejected installation can be updated and re-submitted.";
            return RedirectToAction(nameof(Index));
        }

        var incoming = BuildPhotoList(null, completionPhotos);
        if (incoming.Count == 0)
        {
            TempData["Warning"] = "Please attach the corrected photos before re-submitting.";
            return RedirectToAction(nameof(Index));
        }

        // Cap counts the photos already on the record — the new set is added to
        // them, not swapped in, so admin can see what changed.
        var existingCount = (await _uow.InstallationPhotos
                                 .FindAsync(p => p.InstallationId == installation.Id)).Count();
        if (existingCount + incoming.Count > InstallationPhoto.MaxPerInstallation)
        {
            TempData["Warning"] = $"This installation already has {existingCount} photo(s). " +
                                  $"You can add at most {InstallationPhoto.MaxPerInstallation - existingCount} more.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var saved = await SavePhotosAsync(installation, installation.SolarRequestId, wid, incoming);
            if (saved.Count == 0)
            {
                TempData["Warning"] = "Photos could not be uploaded. Please try again.";
                return RedirectToAction(nameof(Index));
            }

            if (!string.IsNullOrWhiteSpace(notes)) installation.Notes = notes;
            installation.ApprovalStatus = ApprovalStatus.Pending;
            installation.RejectionReason = null;
            installation.ReviewedAt = null;
            installation.ReviewedBy = null;
            installation.SubmittedAt = DateTime.UtcNow;
            _uow.Installations.Update(installation);
            await _uow.SaveChangesAsync();

            TempData["Success"] = $"{saved.Count} photo(s) added. Sent back to admin for approval.";
        }
        catch (Exception ex)
        {
            TempData["Warning"] = $"Re-submit failed: {ex.InnerException?.Message ?? ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Merges the legacy single-file field and the new multi-file field into one
    /// list of real (non-empty) uploads, so both post shapes are handled the same.
    /// </summary>
    private static List<IFormFile> BuildPhotoList(IFormFile? single, List<IFormFile>? many)
    {
        var list = new List<IFormFile>();
        if (many != null) list.AddRange(many.Where(f => f != null && f.Length > 0));
        if (single != null && single.Length > 0 &&
            !list.Any(f => f.FileName == single.FileName && f.Length == single.Length))
        {
            list.Add(single);
        }
        return list;
    }

    /// <summary>
    /// Stores each photo under uploads/installation/&lt;requestId&gt;/ and writes one
    /// InstallationPhoto row per file. Returns the saved paths in upload order.
    /// A file that fails to upload is skipped rather than failing the whole batch —
    /// the installer would otherwise lose 29 good photos over one bad one.
    /// </summary>
    private async Task<List<string>> SavePhotosAsync(
        Installation installation, int requestId, int workerId, List<IFormFile> files)
    {
        var saved = new List<string>();
        foreach (var f in files)
        {
            var (ok, path, _) = await _fileUpload.UploadAsync(f, $"installation/{requestId}");
            if (!ok || string.IsNullOrWhiteSpace(path)) continue;

            await _uow.InstallationPhotos.AddAsync(new InstallationPhoto
            {
                InstallationId     = installation.Id,
                SolarRequestId     = requestId,
                FilePath           = path!,
                FileName           = Path.GetFileNameWithoutExtension(f.FileName),
                ContentType        = f.ContentType,
                FileSizeBytes      = f.Length,
                UploadedByWorkerId = workerId
            });
            saved.Add(path!);
        }
        if (saved.Count > 0) await _uow.SaveChangesAsync();
        return saved;
    }

    /// <summary>
    /// Image point 11: "Admin ko approve hone par credit hona chahiye."
    ///
    /// The approve/reject buttons live in the ADMIN app (separate app, shared DB),
    /// which flips Installations.ApprovalStatus. This sweep runs whenever the
    /// installer opens their queue and pays out any installation admin has since
    /// approved. Two guards make it safe to run on every page load:
    ///   • CommissionCredited on the row, and
    ///   • IncWalletService's own per-request ledger check.
    /// JOB workers are never paid — IncWalletService re-reads Workers.Type itself.
    ///
    /// Returns a message for the installer, or null when nothing was credited.
    /// </summary>
    private async Task<string?> CreditApprovedInstallationsAsync(int workerId)
    {
        var pending = (await _uow.Installations.FindAsync(i =>
                           i.AssignedWorkerId == workerId &&
                           i.IsCompleted &&
                           i.ApprovalStatus == ApprovalStatus.Approved &&
                           !i.CommissionCredited)).ToList();
        if (pending.Count == 0) return null;

        var worker = await _uow.Workers.GetByIdAsync(workerId);
        var messages = new List<string>();

        foreach (var inst in pending)
        {
            // Mark first, credit second? No — credit first so a failure leaves the
            // row untouched and the next sweep retries. CreditInstallationCommissionAsync
            // is idempotent per request, so a retry can't double-pay.
            if (worker != null && worker.Type == WorkerType.INC)
            {
                try
                {
                    var res = await _incWallet.CreditInstallationCommissionAsync(
                        inst.SolarRequestId, workerId, "worker-" + workerId);
                    if (!string.IsNullOrWhiteSpace(res.Message)) messages.Add(res.Message);
                }
                catch (Exception ex)
                {
                    // Money problems must never break the queue page.
                    messages.Add($"Commission could not be credited: {ex.InnerException?.Message ?? ex.Message}");
                    continue;   // leave CommissionCredited false so we retry next time
                }
            }

            inst.CommissionCredited = true;
            _uow.Installations.Update(inst);
        }

        await _uow.SaveChangesAsync();
        return messages.Count > 0 ? string.Join(" ", messages) : null;
    }

    /// <summary>One row of the installer's queue - request plus whatever records exist for it.</summary>
    public class InstallationRow
    {
        public SolarRequest Request { get; set; } = null!;
        public Installation? Installation { get; set; }
        public MaterialDispatch? Dispatch { get; set; }

        /// <summary>Every photo the installer attached for this installation (image point 11).</summary>
        public List<InstallationPhoto> Photos { get; set; } = new();

        public bool IsCompleted => Installation?.IsCompleted == true;
        public string? AdminRemark => Installation?.Remark;
        public DateTime? DispatchDate => Dispatch?.DispatchDate;
        public string? MaterialDetails => Dispatch?.MaterialDetails;
        public string? VehicleDetails => Dispatch?.VehicleDetails;

        /// <summary>Admin's verdict on the marked installation. Pending until reviewed.</summary>
        public ApprovalStatus Verdict => Installation?.ApprovalStatus ?? ApprovalStatus.Pending;
        public bool IsRejected => IsCompleted && Verdict == ApprovalStatus.Rejected;
        public bool IsApproved => IsCompleted && Verdict == ApprovalStatus.Approved;
        public string? RejectionReason => Installation?.RejectionReason;
    }
}
