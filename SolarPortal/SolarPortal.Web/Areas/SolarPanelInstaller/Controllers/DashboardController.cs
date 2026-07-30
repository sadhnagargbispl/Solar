using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;
using SolarPortal.Web.ViewModels;

namespace SolarPortal.Web.Areas.SolarPanelInstaller.Controllers;

// Installer / INC panel. Accessible only to users in the "Installer" role.
// Login is unified through the user-panel login page (Account/Login) which
// routes Installer users here.
[Area("SolarPanelInstaller")]
[Authorize(Roles = "Installer")]
public class DashboardController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IIncWalletService _incWallet;
    public DashboardController(ApplicationDbContext db, IIncWalletService incWallet)
    {
        _db = db;
        _incWallet = incWallet;
    }

    private int WorkerId => int.TryParse(User.FindFirst("WorkerId")?.Value, out var id) ? id : 0;
    private bool IsInc => User.FindFirst("WorkerType")?.Value == "INC";

    public async Task<IActionResult> Index()
    {
        var vm = new InstallerDashboardViewModel
        {
            IsInc = IsInc,
            WorkerName = User.Identity?.Name ?? "Installer"
        };

        var wid = WorkerId;
        if (wid <= 0) return View(vm);

        // ---- 1. Connections this worker registered himself (Reports page) ----
        var inc = await _db.IncConnections
            .Where(c => c.WorkerId == wid && !c.IsDeleted)
            .Select(c => new { c.Status, c.CommissionAmount })
            .ToListAsync();

        vm.RegisteredConnections = inc.Count;
        // Pending AND Complete are both still sitting with the admin - only
        // Approved / Rejected are decided.
        vm.RegisteredAwaitingApproval = inc.Count(c => c.Status != "Approved" && c.Status != "Rejected");

        // ---- 2. Projects the ADMIN assigned to this worker ----
        // Same two sources the Installations page builds its queue from: an
        // Installation row (admin saved a remark) and/or a MaterialDispatch row
        // (admin picked the despatch person). One project can have both.
        var myInstalls = await _db.Installations
            .Where(i => i.AssignedWorkerId == wid && !i.IsDeleted)
            .Select(i => new { i.SolarRequestId, i.IsCompleted })
            .ToListAsync();
        var myDispatchIds = await _db.MaterialDispatches
            .Where(m => m.AssignedWorkerId == wid && !m.IsDeleted)
            .Select(m => m.SolarRequestId)
            .ToListAsync();

        var assignedIds = myInstalls.Select(i => i.SolarRequestId).Union(myDispatchIds).ToList();
        vm.AssignedProjects = assignedIds.Count;

        // Pending = not marked complete yet AND the project is actually waiting at
        // the Installation stage - exactly what the Installations page calls pending.
        var completedIds = myInstalls.Where(i => i.IsCompleted).Select(i => i.SolarRequestId).ToHashSet();
        var openIds = assignedIds.Where(id => !completedIds.Contains(id)).ToList();
        if (openIds.Count > 0)
        {
            vm.AssignedPending = await _db.SolarRequests
                .CountAsync(r => openIds.Contains(r.Id) && r.CurrentStage == ProjectStatus.Installation);
        }

        // ---- 3. INC income ----
        if (vm.IsInc)
        {
            // Same two sources as the Withdraw page: approved connections plus the
            // installation commission credited on Mark Complete.
            var gross = inc.Where(c => c.Status == "Approved").Sum(c => c.CommissionAmount);
            var connectionNet = Math.Round(gross * 0.99m, 2);   // after 1% TDS
            var installationNet = await _incWallet.GetLedgerNetAsync(wid);
            vm.NetIncome = connectionNet + installationNet;
            var used = await _db.IncWithdrawals
                .Where(w => w.WorkerId == wid && w.Status != "Rejected")
                .SumAsync(w => (decimal?)w.Amount) ?? 0m;
            vm.AvailableToWithdraw = Math.Max(0m, vm.NetIncome - used);
        }

        return View(vm);
    }
}
