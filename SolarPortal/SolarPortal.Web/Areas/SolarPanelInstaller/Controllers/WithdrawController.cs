using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Web.Areas.SolarPanelInstaller.Controllers;

// Withdrawal requests - INC workers only.
[Area("SolarPanelInstaller")]
[Authorize(Roles = "Installer")]
public class WithdrawController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IIncWalletService _incWallet;
    private readonly IBankService _banks;
    public WithdrawController(ApplicationDbContext db, IIncWalletService incWallet, IBankService banks)
    {
        _db = db;
        _incWallet = incWallet;
        _banks = banks;
    }

    private int WorkerId => int.TryParse(User.FindFirst("WorkerId")?.Value, out var id) ? id : 0;
    private bool IsInc => User.FindFirst("WorkerType")?.Value == "INC";

    // Two things earn an INC worker money and both feed this balance:
    //   1. Connections he registered himself, once admin approves them.
    //   2. Installation commission credited on "Mark Complete" (IncCommissionLedger,
    //      mirrored into the legacy TrnVoucher INC wallet).
    // Withdrawals that are not rejected - pending or already paid - are held back.
    // Only the resulting available balance is shown; the gross/net breakdown is not.
    private async Task<decimal> ComputeAvailableAsync(int wid)
    {
        var gross = await _db.IncConnections
            .Where(c => c.WorkerId == wid && !c.IsDeleted && c.Status == "Approved")
            .SumAsync(c => (decimal?)c.CommissionAmount) ?? 0m;
        var connectionNet = System.Math.Round(gross * 0.99m, 2);   // after 1% TDS

        // Already net of TDS when it was credited.
        var installationNet = await _incWallet.GetLedgerNetAsync(wid);

        var net = connectionNet + installationNet;
        var used = await _db.IncWithdrawals
            .Where(w => w.WorkerId == wid && w.Status != "Rejected")
            .SumAsync(w => (decimal?)w.Amount) ?? 0m;
        return System.Math.Max(0m, net - used);
    }

    public async Task<IActionResult> Index()
    {
        if (!IsInc)
        {
            TempData["Warning"] = "Withdrawal is available to INC workers only.";
            return RedirectToAction("Index", "Dashboard");
        }
        var wid = WorkerId;
        ViewBag.Available = await ComputeAvailableAsync(wid);
        ViewBag.Banks = await _banks.GetActiveAsync();
        var list = await _db.IncWithdrawals
            .Where(w => w.WorkerId == wid)
            .OrderByDescending(w => w.RequestedAt)
            .ToListAsync();
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Request(decimal amount, string? bankName, string? ifsCode,
                                             string? branchName, string? accountNo)
    {
        if (!IsInc) { TempData["Warning"] = "INC workers only."; return RedirectToAction("Index", "Dashboard"); }
        var wid = WorkerId;
        var available = await ComputeAvailableAsync(wid);

        bankName = bankName?.Trim();
        ifsCode = ifsCode?.Trim().ToUpperInvariant();
        branchName = branchName?.Trim();
        accountNo = accountNo?.Trim();

        if (amount <= 0)
            TempData["Error"] = "Enter a valid amount.";
        else if (amount > available)
            TempData["Error"] = $"Requested {amount:N2} exceeds available balance {available:N2}.";
        else if (string.IsNullOrWhiteSpace(bankName))
            TempData["Error"] = "Select your bank.";
        else if (string.IsNullOrWhiteSpace(accountNo))
            TempData["Error"] = "Enter your account number.";
        else if (string.IsNullOrWhiteSpace(ifsCode))
            TempData["Error"] = "Enter the IFSC code.";
        else
        {
            var reqNo = "IWD-" + System.DateTime.UtcNow.ToString("yyMMddHHmmss");

            // BankDetails predates these columns; keep it filled so reports that
            // only read the old column still show something useful.
            var summary = string.Join(" | ", new[]
            {
                bankName,
                string.IsNullOrWhiteSpace(branchName) ? null : branchName,
                "A/c " + accountNo,
                "IFSC " + ifsCode
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            _db.IncWithdrawals.Add(new IncWithdrawal
            {
                WorkerId = wid,
                Amount = amount,
                BankDetails = summary,
                BankName = bankName,
                IFSCode = ifsCode,
                BranchName = branchName,
                AccountNo = accountNo,
                Status = "Pending",
                RequestedAt = System.DateTime.UtcNow,
                RequestNumber = reqNo
            });
            await _db.SaveChangesAsync();

            // Money leaves the wallet the moment it is requested, so the same balance
            // cannot be requested twice while the first request is still pending.
            // A wallet problem must not lose the request - report it and carry on.
            var walletMsg = string.Empty;
            try
            {
                var (posted, msg) = await _incWallet.DebitForWithdrawalAsync(wid, amount, reqNo);
                if (!posted) walletMsg = " " + msg;
            }
            catch (System.Exception ex)
            {
                walletMsg = " Wallet debit failed: " + (ex.InnerException?.Message ?? ex.Message);
            }

            TempData["Success"] = "Withdrawal request submitted. Awaiting admin approval." + walletMsg;
        }
        return RedirectToAction(nameof(Index));
    }
}
