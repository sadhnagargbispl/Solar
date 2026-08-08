using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
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

    /// <summary>
    /// KYC gate on WITHDRAWALS: "mark installation to kar sakta bina KYC, par
    /// withdrawal nahi hoga jab tak KYC approve na ho."
    ///
    /// So an INC works and earns freely; only taking the money out waits on KYC.
    /// All THREE sections must be Approved - and the Bank section especially,
    /// because that is the account the payout is sent to. Paying into a bank
    /// account nobody verified is exactly what this rule exists to stop.
    ///
    /// Returns the message to show, or null when the withdrawal may proceed.
    /// </summary>
    private async Task<string?> KycBlockAsync(int workerId)
    {
        // Read the type from the DB rather than the auth cookie: the cookie is
        // stamped at login and goes stale the moment admin changes a worker's
        // type, and this decides whether the rule applies at all.
        var worker = await _db.Workers.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workerId);
        if (worker == null || worker.Type != WorkerType.INC) return null;

        var kyc = await _db.IncKycDocuments.AsNoTracking()
                           .Where(k => k.WorkerId == workerId)
                           .OrderByDescending(k => k.Id)
                           .FirstOrDefaultAsync();

        if (kyc == null)
            return "You have not submitted your KYC yet. Withdrawals open once it is approved — " +
                   "open My KYC to upload it.";

        if (kyc.IsFullyApproved) return null;

        // Name what is actually outstanding, so the installer knows whether to
        // re-upload something or simply wait for the admin.
        var pending = new List<string>();
        if (kyc.AddressStatus != ApprovalStatus.Approved) pending.Add($"Address Proof ({kyc.AddressStatus})");
        if (kyc.BankStatus != ApprovalStatus.Approved) pending.Add($"Bank Detail ({kyc.BankStatus})");
        if (kyc.PanStatus != ApprovalStatus.Approved) pending.Add($"PAN Card ({kyc.PanStatus})");

        return "Your KYC is not fully approved yet — " + string.Join(", ", pending) +
               ". Withdrawals open once all three sections are approved. Your earnings stay safe until then.";
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

    /// <summary>Rows per page on the Withdrawal Report.</summary>
    private const int ReportPageSize = 10;

    // The request form only. Past requests moved to their own paged page
    // (Report) so the menu can link straight to it.
    public async Task<IActionResult> Index()
    {
        // Surfaced on the form as well - a Request button that always fails is
        // worse than one that explains itself before being pressed.
        ViewBag.KycBlock = await KycBlockAsync(WorkerId);

        if (!IsInc)
        {
            TempData["Warning"] = "Withdrawal is available to INC workers only.";
            return RedirectToAction("Index", "Dashboard");
        }
        var wid = WorkerId;
        ViewBag.Available = await ComputeAvailableAsync(wid);
        ViewBag.Banks = await _banks.GetActiveAsync();
        return View();
    }

    // GET: /SolarPanelInstaller/Withdraw/Report?page=2
    // Paged history of this worker's withdrawal requests.
    public async Task<IActionResult> Report(int page = 1)
    {
        if (!IsInc)
        {
            TempData["Warning"] = "Withdrawal is available to INC workers only.";
            return RedirectToAction("Index", "Dashboard");
        }
        var wid = WorkerId;

        var query = _db.IncWithdrawals.Where(w => w.WorkerId == wid);
        var total = await query.CountAsync();

        // Clamp the page so a hand-typed ?page=99 - or a stale link after rows were
        // removed - lands on the last real page instead of an empty table.
        var totalPages = total == 0 ? 1 : (int)System.Math.Ceiling(total / (double)ReportPageSize);
        if (page < 1) page = 1;
        if (page > totalPages) page = totalPages;

        var list = await query
            .OrderByDescending(w => w.RequestedAt)
            .Skip((page - 1) * ReportPageSize)
            .Take(ReportPageSize)
            .ToListAsync();

        ViewBag.Page = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalRows = total;
        ViewBag.PageSize = ReportPageSize;
        ViewBag.Available = await ComputeAvailableAsync(wid);
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Request(decimal amount, string? bankName, string? ifsCode,
                                             string? branchName, string? accountNo)
    {
        if (!IsInc) { TempData["Warning"] = "INC workers only."; return RedirectToAction("Index", "Dashboard"); }
        var wid = WorkerId;

        // KYC gate. Earning is free; taking the money out is not.
        var kycBlock = await KycBlockAsync(wid);
        if (kycBlock != null)
        {
            TempData["Error"] = kycBlock;
            return RedirectToAction(nameof(Index));
        }

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
