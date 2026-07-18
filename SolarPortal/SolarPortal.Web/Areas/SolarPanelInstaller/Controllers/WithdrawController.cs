using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Web.Areas.SolarPanelInstaller.Controllers;

// Withdrawal requests — INC workers only.
[Area("SolarPanelInstaller")]
[Authorize(Roles = "Installer")]
public class WithdrawController : Controller
{
    private readonly ApplicationDbContext _db;
    public WithdrawController(ApplicationDbContext db) { _db = db; }

    private int WorkerId => int.TryParse(User.FindFirst("WorkerId")?.Value, out var id) ? id : 0;
    private bool IsInc => User.FindFirst("WorkerType")?.Value == "INC";

    private async Task<(decimal net, decimal available)> ComputeBalanceAsync(int wid)
    {
        var gross = await _db.IncConnections
            .Where(c => c.WorkerId == wid && !c.IsDeleted && c.Status == "Approved")
            .SumAsync(c => (decimal?)c.CommissionAmount) ?? 0m;
        var net = System.Math.Round(gross * 0.99m, 2);   // after 1% TDS
        var used = await _db.IncWithdrawals
            .Where(w => w.WorkerId == wid && w.Status != "Rejected")
            .SumAsync(w => (decimal?)w.Amount) ?? 0m;
        return (net, System.Math.Max(0m, net - used));
    }

    public async Task<IActionResult> Index()
    {
        if (!IsInc)
        {
            TempData["Warning"] = "Withdrawal is available to INC workers only.";
            return RedirectToAction("Index", "Dashboard");
        }
        var wid = WorkerId;
        var (net, available) = await ComputeBalanceAsync(wid);
        ViewBag.NetIncome = net;
        ViewBag.Available = available;
        var list = await _db.IncWithdrawals
            .Where(w => w.WorkerId == wid)
            .OrderByDescending(w => w.RequestedAt)
            .ToListAsync();
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Request(decimal amount, string? bankDetails)
    {
        if (!IsInc) { TempData["Warning"] = "INC workers only."; return RedirectToAction("Index", "Dashboard"); }
        var wid = WorkerId;
        var (_, available) = await ComputeBalanceAsync(wid);

        if (amount <= 0)
            TempData["Error"] = "Enter a valid amount.";
        else if (amount > available)
            TempData["Error"] = $"Requested {amount:N2} exceeds available balance {available:N2}.";
        else
        {
            _db.IncWithdrawals.Add(new IncWithdrawal
            {
                WorkerId = wid,
                Amount = amount,
                BankDetails = bankDetails,
                Status = "Pending",
                RequestedAt = System.DateTime.UtcNow,
                RequestNumber = "IWD-" + System.DateTime.UtcNow.ToString("yyMMddHHmmss")
            });
            await _db.SaveChangesAsync();
            TempData["Success"] = "Withdrawal request submitted. Awaiting admin approval.";
        }
        return RedirectToAction(nameof(Index));
    }
}