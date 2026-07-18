using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Web.Areas.SolarPanelInstaller.Controllers;

// Payout / income — INC workers only.
[Area("SolarPanelInstaller")]
[Authorize(Roles = "Installer")]
public class PayoutController : Controller
{
    private readonly ApplicationDbContext _db;
    public PayoutController(ApplicationDbContext db) { _db = db; }

    private int WorkerId => int.TryParse(User.FindFirst("WorkerId")?.Value, out var id) ? id : 0;
    private bool IsInc => User.FindFirst("WorkerType")?.Value == "INC";

    public async Task<IActionResult> Index()
    {
        if (!IsInc)
        {
            TempData["Warning"] = "Payout is available to INC workers only.";
            return RedirectToAction("Index", "Dashboard");
        }
        var wid = WorkerId;
        var approved = await _db.IncConnections
            .Where(c => c.WorkerId == wid && !c.IsDeleted && c.Status == "Approved")
            .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt)
            .ToListAsync();
        return View(approved);
    }
}