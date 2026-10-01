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
        var entriesByInstall = installIds.Count == 0
            ? new Dictionary<int, List<InstallationChecklistEntry>>()
            : (await _uow.InstallationChecklistEntries.FindAsync(e => installIds.Contains(e.InstallationId)))
              .GroupBy(e => e.InstallationId)
              .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id).ToList());

        // The fixed INC upload format the Mark / Update forms are built from.
        var formats = await LoadFormatsAsync();
        ViewBag.Formats = formats;

        var allRows = requestIds
            .Where(requests.ContainsKey)
            .Select(id =>
            {
                myInstalls.TryGetValue(id, out var inst);
                myDispatches.TryGetValue(id, out var disp);
                var photos = inst != null && photosByInstall.TryGetValue(inst.Id, out var ph)
                                ? ph
                                : new List<InstallationPhoto>();
                var entries = inst != null && entriesByInstall.TryGetValue(inst.Id, out var en)
                                ? en
                                : new List<InstallationChecklistEntry>();
                return new InstallationRow
                {
                    Request = requests[id],
                    Installation = inst,
                    Dispatch = disp,
                    Photos = photos,
                    Entries = entries,
                    Checklist = InstallationChecklist.Evaluate(formats, photos, entries)
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

    // GET: /SolarPanelInstaller/Installation/Details/5   (5 = Installations.Id)
    // Everything the installer submitted for one installation, laid out per
    // checklist item: which photos belong to which item, the video, the details.
    public async Task<IActionResult> Details(int id)
    {
        var wid = WorkerId;
        var installation = await _uow.Installations.GetByIdAsync(id);
        if (installation == null || installation.AssignedWorkerId != wid)
        {
            TempData["Warning"] = "Installation not found.";
            return RedirectToAction(nameof(Index));
        }

        var request = await _uow.SolarRequests.GetByIdAsync(installation.SolarRequestId);
        if (request == null)
        {
            TempData["Warning"] = "Request not found.";
            return RedirectToAction(nameof(Index));
        }

        var dispatch = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == request.Id))
                       .OrderByDescending(m => m.CreatedAt)
                       .FirstOrDefault();
        var photos = (await _uow.InstallationPhotos.FindAsync(p => p.InstallationId == id))
                     .OrderBy(p => p.Id).ToList();
        var entries = (await _uow.InstallationChecklistEntries.FindAsync(e => e.InstallationId == id))
                      .OrderBy(e => e.Id).ToList();
        var formats = await LoadFormatsAsync();

        return View(new InstallationRow
        {
            Request = request,
            Installation = installation,
            Dispatch = dispatch,
            Photos = photos,
            Entries = entries,
            Checklist = InstallationChecklist.Evaluate(formats, photos, entries)
        });
    }

    // POST: /SolarPanelInstaller/Installation/MarkComplete
    // Mirrors the admin flow that used to live in OperationsController.SubmitInstallation:
    // completes the Installation row, logs the WorkerAssignment and advances the stage
    // (Domestic -> DCR Update, Commercial -> Completed).
    //
    // The installer files the FIXED INC upload format (IncUploadFormats): every line's
    // photos (min..max), the video and the typed details. Nothing is saved unless the
    // whole format is filled — "ye pura fill hoga tabhi INC commission de sakta hai".
    // The installation then goes to admin as Pending; commission waits for approval,
    // and a rejected installation is corrected through Resubmit below.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxSubmissionBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxSubmissionBytes)]
    public async Task<IActionResult> MarkComplete(int requestId, DateTime? installationDate,
        string? notes, string? remark)
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

        var formats = await LoadFormatsAsync();
        if (formats.Count == 0)
        {
            TempData["Warning"] = "The INC upload format is not set up yet. Please contact the admin.";
            return RedirectToAction(nameof(Index));
        }

        // Validate the WHOLE format before a single file is written.
        var sub = ReadSubmission(formats);
        var problems = FileProblems(formats, sub);
        problems.AddRange(formats
            .Select(f => InstallationChecklist.ProblemFor(
                f, sub.Photos[f.Id].Count, sub.Videos[f.Id].Count, sub.Remarks[f.Id]))
            .Where(p => p != null)!);
        if (problems.Count > 0)
        {
            TempData["Warning"] = "Upload format is not complete — nothing was saved. " +
                                  string.Join(" | ", problems);
            return RedirectToAction(nameof(Index));
        }

        // Files go up before any DB change, so a failed upload can't leave a
        // half-filled installation marked complete.
        var (uploaded, uploadError) = await UploadChecklistFilesAsync(requestId, formats, sub);
        if (uploadError != null)
        {
            TempData["Warning"] = $"Upload failed — nothing was saved. {uploadError}";
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

            // Checklist rows need the Installation.Id, so they are stored right after.
            await WriteChecklistRowsAsync(installation, wid, uploaded, sub);

            // Keep the legacy single-photo column pointing at the first photo so
            // every existing screen that reads it keeps rendering something.
            var firstPhoto = uploaded.FirstOrDefault(u => !u.IsVideo);
            if (firstPhoto != null)
            {
                installation.CompletionPhotoPath = firstPhoto.Path;
                _uow.Installations.Update(installation);
                await _uow.SaveChangesAsync();
            }

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
    // Admin rejected it — the installer corrects the upload format: any line they
    // upload new photos / video / detail for REPLACES that line's old set; lines
    // left blank keep what is already there. The result must again fill the whole
    // format, then the reject reason is cleared and it goes back to admin.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxSubmissionBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxSubmissionBytes)]
    public async Task<IActionResult> Resubmit(int installationId, string? notes)
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

        var formats = await LoadFormatsAsync();
        if (formats.Count == 0)
        {
            TempData["Warning"] = "The INC upload format is not set up yet. Please contact the admin.";
            return RedirectToAction(nameof(Index));
        }

        var sub = ReadSubmission(formats);
        if (!sub.HasAnything)
        {
            TempData["Warning"] = "Please upload the corrected photos / video / details before re-submitting.";
            return RedirectToAction(nameof(Index));
        }

        // What the installation WILL hold once this submission replaces its lines.
        var existingPhotos = (await _uow.InstallationPhotos
                                  .FindAsync(p => p.InstallationId == installation.Id)).ToList();
        var existingEntries = (await _uow.InstallationChecklistEntries
                                   .FindAsync(e => e.InstallationId == installation.Id)).ToList();
        var current = InstallationChecklist.Evaluate(formats, existingPhotos, existingEntries)
                                           .ToDictionary(l => l.Format.Id);

        var problems = FileProblems(formats, sub);
        problems.AddRange(formats
            .Select(f => InstallationChecklist.ProblemFor(f,
                sub.Photos[f.Id].Count > 0 ? sub.Photos[f.Id].Count : current[f.Id].Photos,
                sub.Videos[f.Id].Count > 0 ? sub.Videos[f.Id].Count : current[f.Id].Videos,
                sub.Remarks[f.Id].Length > 0 ? sub.Remarks[f.Id] : current[f.Id].Remark))
            .Where(p => p != null)!);
        if (problems.Count > 0)
        {
            TempData["Warning"] = "Upload format is still not complete — nothing was saved. " +
                                  string.Join(" | ", problems);
            return RedirectToAction(nameof(Index));
        }

        var (uploaded, uploadError) = await UploadChecklistFilesAsync(installation.SolarRequestId, formats, sub);
        if (uploadError != null)
        {
            TempData["Warning"] = $"Upload failed — nothing was saved. {uploadError}";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            // Replace, line by line, whatever the installer re-filed. Soft delete,
            // so the admin's query filter hides the old set and history survives.
            foreach (var f in formats)
            {
                if (sub.Photos[f.Id].Count > 0)
                    foreach (var p in existingPhotos.Where(p => p.FormatItemId == f.Id))
                    {
                        p.IsDeleted = true;
                        _uow.InstallationPhotos.Update(p);
                    }
                foreach (var e in existingEntries.Where(e => e.FormatItemId == f.Id &&
                             ((e.EntryType == ChecklistEntryType.Video && sub.Videos[f.Id].Count > 0) ||
                              (e.EntryType == ChecklistEntryType.Remark && sub.Remarks[f.Id].Length > 0))))
                {
                    e.IsDeleted = true;
                    _uow.InstallationChecklistEntries.Update(e);
                }
            }
            await WriteChecklistRowsAsync(installation, wid, uploaded, sub);

            var firstPhoto = uploaded.FirstOrDefault(u => !u.IsVideo);
            if (firstPhoto != null) installation.CompletionPhotoPath = firstPhoto.Path;
            if (!string.IsNullOrWhiteSpace(notes)) installation.Notes = notes;
            installation.ApprovalStatus = ApprovalStatus.Pending;
            installation.RejectionReason = null;
            installation.ReviewedAt = null;
            installation.ReviewedBy = null;
            installation.SubmittedAt = DateTime.UtcNow;
            _uow.Installations.Update(installation);
            await _uow.SaveChangesAsync();

            TempData["Success"] = "Upload format updated. Sent back to admin for approval.";
        }
        catch (Exception ex)
        {
            TempData["Warning"] = $"Re-submit failed: {ex.InnerException?.Message ?? ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    // 39 photos x 10 MB + a 200 MB video, with headroom. IIS has its own cap in web.config.
    private const long MaxSubmissionBytes = 1024L * 1024 * 1024;   // 1 GB
    private const long MaxPhotoBytes = 10L * 1024 * 1024;          // same as FileUploadService
    private static readonly string[] PhotoExtensions = { ".jpg", ".jpeg", ".png" };

    private async Task<List<IncUploadFormat>> LoadFormatsAsync() =>
        (await _uow.IncUploadFormats.FindAsync(f => f.IsActive)).OrderBy(f => f.SrNo).ToList();

    /// <summary>What the installer posted for each format line.</summary>
    private sealed class ChecklistSubmission
    {
        public Dictionary<int, List<IFormFile>> Photos { get; } = new();
        public Dictionary<int, List<IFormFile>> Videos { get; } = new();
        public Dictionary<int, string> Remarks { get; } = new();

        public bool HasAnything =>
            Photos.Values.Any(l => l.Count > 0) || Videos.Values.Any(l => l.Count > 0) ||
            Remarks.Values.Any(r => r.Length > 0);
    }

    /// <summary>A file already stored on disk, waiting for its DB row.</summary>
    private sealed record UploadedFile(int FormatItemId, IFormFile File, string Path, bool IsVideo);

    // Field names per line: photos_{id} (multiple), video_{id}, remark_{id}.
    private ChecklistSubmission ReadSubmission(List<IncUploadFormat> formats)
    {
        var form = Request.Form;
        var sub = new ChecklistSubmission();
        foreach (var f in formats)
        {
            sub.Photos[f.Id] = f.MaxPhotos > 0
                ? form.Files.GetFiles($"photos_{f.Id}").Where(x => x.Length > 0).ToList()
                : new List<IFormFile>();
            sub.Videos[f.Id] = f.VideoCount > 0
                ? form.Files.GetFiles($"video_{f.Id}").Where(x => x.Length > 0).ToList()
                : new List<IFormFile>();
            var text = f.RemarkRequired ? form[$"remark_{f.Id}"].ToString().Trim() : "";
            sub.Remarks[f.Id] = text.Length > 1000 ? text[..1000] : text;
        }
        return sub;
    }

    /// <summary>Wrong file type / too big — checked up front so nothing is half-saved.</summary>
    private static List<string> FileProblems(List<IncUploadFormat> formats, ChecklistSubmission sub)
    {
        var issues = new List<string>();
        foreach (var f in formats)
        {
            foreach (var p in sub.Photos[f.Id])
            {
                var ext = Path.GetExtension(p.FileName).ToLowerInvariant();
                if (!PhotoExtensions.Contains(ext))
                    issues.Add($"{f.SrNo}. {p.FileName} is not a JPG / PNG photo");
                else if (p.Length > MaxPhotoBytes)
                    issues.Add($"{f.SrNo}. {p.FileName} is larger than 10 MB");
            }
            foreach (var v in sub.Videos[f.Id])
            {
                var ext = Path.GetExtension(v.FileName).ToLowerInvariant();
                if (!FileUploadService.VideoExtensions.Contains(ext))
                    issues.Add($"{f.SrNo}. {v.FileName} is not a video (MP4 / MOV / 3GP / WEBM / MKV / M4V)");
                else if (v.Length > FileUploadService.MaxVideoBytes)
                    issues.Add($"{f.SrNo}. {v.FileName} is larger than {FileUploadService.MaxVideoBytes / (1024 * 1024)} MB");
            }
        }
        return issues;
    }

    /// <summary>
    /// Stores every posted photo / video under uploads/installation/&lt;requestId&gt;/.
    /// All-or-nothing: on the first failure the files already stored are deleted
    /// and the error is returned, so the installer re-submits a complete set.
    /// </summary>
    private async Task<(List<UploadedFile> Uploaded, string? Error)> UploadChecklistFilesAsync(
        int requestId, List<IncUploadFormat> formats, ChecklistSubmission sub)
    {
        var uploaded = new List<UploadedFile>();
        var folder = $"installation/{requestId}";
        foreach (var f in formats)
        {
            foreach (var (file, isVideo) in sub.Photos[f.Id].Select(x => (x, false))
                                             .Concat(sub.Videos[f.Id].Select(x => (x, true))))
            {
                var (ok, path, err) = isVideo
                    ? await _fileUpload.UploadVideoAsync(file, folder)
                    : await _fileUpload.UploadAsync(file, folder);
                if (!ok || string.IsNullOrWhiteSpace(path))
                {
                    foreach (var u in uploaded) _fileUpload.DeleteFile(u.Path);
                    return (new List<UploadedFile>(), $"{f.SrNo}. {file.FileName}: {err ?? "upload failed"}");
                }
                uploaded.Add(new UploadedFile(f.Id, file, path!, isVideo));
            }
        }
        return (uploaded, null);
    }

    /// <summary>
    /// Writes the DB rows for an uploaded checklist: photos into InstallationPhotos
    /// (tagged with their line, so the admin approval page shows them as before),
    /// videos and typed details into InstallationChecklistEntries.
    /// </summary>
    private async Task WriteChecklistRowsAsync(Installation installation, int workerId,
        List<UploadedFile> uploaded, ChecklistSubmission sub)
    {
        foreach (var u in uploaded)
        {
            if (u.IsVideo)
            {
                await _uow.InstallationChecklistEntries.AddAsync(new InstallationChecklistEntry
                {
                    InstallationId     = installation.Id,
                    SolarRequestId     = installation.SolarRequestId,
                    FormatItemId       = u.FormatItemId,
                    EntryType          = ChecklistEntryType.Video,
                    FilePath           = u.Path,
                    FileName           = Path.GetFileNameWithoutExtension(u.File.FileName),
                    ContentType        = u.File.ContentType,
                    FileSizeBytes      = u.File.Length,
                    UploadedByWorkerId = workerId
                });
            }
            else
            {
                await _uow.InstallationPhotos.AddAsync(new InstallationPhoto
                {
                    InstallationId     = installation.Id,
                    SolarRequestId     = installation.SolarRequestId,
                    FormatItemId       = u.FormatItemId,
                    FilePath           = u.Path,
                    FileName           = Path.GetFileNameWithoutExtension(u.File.FileName),
                    ContentType        = u.File.ContentType,
                    FileSizeBytes      = u.File.Length,
                    UploadedByWorkerId = workerId
                });
            }
        }

        foreach (var (formatId, text) in sub.Remarks.Where(r => r.Value.Length > 0))
        {
            await _uow.InstallationChecklistEntries.AddAsync(new InstallationChecklistEntry
            {
                InstallationId     = installation.Id,
                SolarRequestId     = installation.SolarRequestId,
                FormatItemId       = formatId,
                EntryType          = ChecklistEntryType.Remark,
                RemarkText         = text,
                UploadedByWorkerId = workerId
            });
        }
        await _uow.SaveChangesAsync();
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

        // Fixed INC upload format gate: no commission until every line is filled.
        var formats = await LoadFormatsAsync();
        var ids = pending.Select(i => i.Id).ToHashSet();
        var photos = (await _uow.InstallationPhotos.FindAsync(p => ids.Contains(p.InstallationId))).ToList();
        var entries = (await _uow.InstallationChecklistEntries.FindAsync(e => ids.Contains(e.InstallationId))).ToList();
        var reqIds = pending.Select(i => i.SolarRequestId).ToHashSet();
        var reqNumbers = (await _uow.SolarRequests.FindAsync(r => reqIds.Contains(r.Id)))
                         .ToDictionary(r => r.Id, r => r.RequestNumber);

        foreach (var inst in pending)
        {
            if (worker != null && worker.Type == WorkerType.INC)
            {
                var missing = formats.Count == 0
                    ? 1
                    : InstallationChecklist.Problems(InstallationChecklist.Evaluate(formats,
                          photos.Where(p => p.InstallationId == inst.Id),
                          entries.Where(e => e.InstallationId == inst.Id))).Count;
                if (missing > 0)
                {
                    // Leave CommissionCredited false: once the format is filled the
                    // next sweep pays it.
                    messages.Add($"Commission on hold for {reqNumbers.GetValueOrDefault(inst.SolarRequestId, "#" + inst.SolarRequestId)}: " +
                                 $"the INC upload format is not complete ({missing} item(s) missing).");
                    continue;
                }
            }

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

        /// <summary>Videos + typed details filed against the fixed upload format.</summary>
        public List<InstallationChecklistEntry> Entries { get; set; } = new();

        /// <summary>Per-line state of the fixed INC upload format.</summary>
        public List<InstallationChecklist.LineStatus> Checklist { get; set; } = new();
        public int ChecklistDone => Checklist.Count(l => l.IsComplete);

        /// <summary>
        /// False for installations submitted before the checklist existed — they
        /// have no tagged photos / entries, so showing "0 / 13" for them is wrong.
        /// </summary>
        public bool UsesChecklist => Photos.Any(p => p.FormatItemId != null) || Entries.Any();

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
