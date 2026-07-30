using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Services;

namespace SolarPortal.Web.Areas.SolarPanelInstaller.Controllers;

// INC Wallet statement - INC workers only.
// Route stays /SolarPanelInstaller/Payout so existing links keep working; the
// page itself is now the wallet passbook (narration / amount / date / voucher no)
// instead of the old per-connection payout list.
[Area("SolarPanelInstaller")]
[Authorize(Roles = "Installer")]
public class PayoutController : Controller
{
    private readonly IIncWalletService _incWallet;
    public PayoutController(IIncWalletService incWallet) { _incWallet = incWallet; }

    private int WorkerId => int.TryParse(User.FindFirst("WorkerId")?.Value, out var id) ? id : 0;
    private bool IsInc => User.FindFirst("WorkerType")?.Value == "INC";

    public async Task<IActionResult> Index()
    {
        if (!IsInc)
        {
            TempData["Warning"] = "Wallet is available to INC workers only.";
            return RedirectToAction("Index", "Dashboard");
        }

        var wid = WorkerId;
        var entries = await _incWallet.GetWalletEntriesAsync(wid);

        ViewBag.Balance = await _incWallet.GetWalletBalanceAsync(wid);
        return View(entries);
    }
}
