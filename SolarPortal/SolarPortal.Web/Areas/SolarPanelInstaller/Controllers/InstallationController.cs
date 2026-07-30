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

    private int WorkerId => int.TryParse(User.FindFirst("WorkerId")?.Value, out var id) ? id : 0;

    // GET: /SolarPanelInstaller/Installation
    // ?filter=pending | done | all   (default all, same as the admin reports)
    public async Task<IActionResult> Index(string? filter)
    {
        var wid = WorkerId;
        var f = (filter ?? "all").ToLowerInvariant();
        ViewBag.Filter = f;

        if (wid <= 0) return View(new List<InstallationRow>());

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

        var rows = requestIds
            .Where(requests.ContainsKey)
            .Select(id =>
            {
                myInstalls.TryGetValue(id, out var inst);
                myDispatches.TryGetValue(id, out var disp);
                return new InstallationRow
                {
                    Request = requests[id],
                    Installation = inst,
                    Dispatch = disp
                };
            })
            // Actionable = project abhi Installation stage par hai aur complete nahi hua.
            .Where(row => f switch
            {
                "pending" => !row.IsCompleted && row.Request.CurrentStage == ProjectStatus.Installation,
                "done" => row.IsCompleted,
                _ => true
            })
            .OrderByDescending(row => row.Request.CreatedAt)
            .ToList();

        ViewBag.PendingCount = rows.Count(r => !r.IsCompleted && r.Request.CurrentStage == ProjectStatus.Installation);
        return View(rows);
    }

    // POST: /SolarPanelInstaller/Installation/MarkComplete
    // Mirrors the admin flow that used to live in OperationsController.SubmitInstallation:
    // completes the Installation row, logs the WorkerAssignment and advances the stage
    // (Domestic -> DCR Update, Commercial -> Completed).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkComplete(int requestId, DateTime? installationDate,
        string? notes, string? remark, IFormFile? completionPhoto)
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

        try
        {
            string? photoPath = null;
            if (completionPhoto != null && completionPhoto.Length > 0)
            {
                var (ok, path, err) = await _fileUpload.UploadAsync(completionPhoto, "installation");
                if (!ok)
                {
                    TempData["Warning"] = $"Photo upload failed: {err}";
                    return RedirectToAction(nameof(Index));
                }
                photoPath = path;
            }

            var isNew = installation == null;
            installation ??= new Installation { SolarRequestId = requestId };

            installation.AssignedWorkerId = wid;
            installation.InstallationDate = installationDate ?? DateTime.UtcNow;
            installation.Notes = notes;
            // Admin ka remark tabhi overwrite karo jab installer ne apna likha ho.
            if (!string.IsNullOrWhiteSpace(remark)) installation.Remark = remark;
            if (photoPath != null) installation.CompletionPhotoPath = photoPath;
            installation.IsCompleted = true;
            installation.CompletedAt = DateTime.UtcNow;

            if (isNew) await _uow.Installations.AddAsync(installation);
            else _uow.Installations.Update(installation);

            // Save first so a new row has its Id before the FK reference below.
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

            // Commission: only an INC worker earns it, the moment he marks the
            // installation complete. JOB workers do the same work on salary and
            // are never credited.
            //
            // The worker type is read from the Workers table, NOT from the
            // WorkerType claim: that claim is stamped into the auth cookie at
            // login, so a worker whose type admin changed afterwards would keep
            // the old answer for the whole session - crediting a JOB worker, or
            // silently denying a real INC worker. IncWalletService re-checks the
            // same thing before it writes, so neither path can pay a JOB worker.
            //
            // A wallet problem must never undo a finished installation - report it
            // and carry on.
            var commissionMsg = string.Empty;
            var worker = await _uow.Workers.GetByIdAsync(wid);
            if (worker != null && worker.Type == WorkerType.INC)
            {
                try
                {
                    var commission = await _incWallet.CreditInstallationCommissionAsync(
                        requestId, wid, "worker-" + wid);
                    commissionMsg = " " + commission.Message;
                }
                catch (Exception cex)
                {
                    commissionMsg = $" Commission could not be credited: {cex.InnerException?.Message ?? cex.Message}";
                }
            }

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

    /// <summary>One row of the installer's queue - request plus whatever records exist for it.</summary>
    public class InstallationRow
    {
        public SolarRequest Request { get; set; } = null!;
        public Installation? Installation { get; set; }
        public MaterialDispatch? Dispatch { get; set; }

        public bool IsCompleted => Installation?.IsCompleted == true;
        public string? AdminRemark => Installation?.Remark;
        public DateTime? DispatchDate => Dispatch?.DispatchDate;
        public string? MaterialDetails => Dispatch?.MaterialDetails;
        public string? VehicleDetails => Dispatch?.VehicleDetails;
    }
}
