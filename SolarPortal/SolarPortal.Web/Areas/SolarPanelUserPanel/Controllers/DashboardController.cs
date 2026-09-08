using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Web.Areas.SolarPanelUserPanel.Helpers;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Controllers;

[Area("SolarPanelUserPanel")]
[Authorize(Roles = "User")]
public class DashboardController : Controller
{
    private readonly IDashboardService _dashboardService;
    private readonly IPaymentService _paymentService;
    private readonly ILegacyProductRequestService _deposits;
    private readonly SolarPortal.Infrastructure.Data.ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public DashboardController(
        IDashboardService dashboardService,
        IPaymentService paymentService,
        ILegacyProductRequestService deposits,
        SolarPortal.Infrastructure.Data.ApplicationDbContext db,
        UserManager<ApplicationUser> userManager)
    {
        _dashboardService = dashboardService;
        _paymentService = paymentService;
        _deposits = deposits;
        _db = db;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var dashboard = await _dashboardService.GetUserDashboardAsync(userId);

        // Surface payment totals for the active project so Dashboard can show
        // the same "Next Action" card the Status page renders.
        if (dashboard.LatestProject != null)
        {
            // Point 1: same deposit adjustment the Status page makes. This card
            // repeats the Status figures, so it has to use the same arithmetic or
            // the two screens disagree about the same project.
            var deposit = dashboard.LatestProject.RequestType == SolarPortal.Domain.Enums.RequestType.AlreadyActiveOnlyRequest
                ? await _deposits.GetApprovedOrderAmountAsync(dashboard.LatestProject.UserId)
                : 0m;
            ViewBag.ActiveIdDeposit = deposit;

            // Same as the Status page - the shared timeline partial reads this.
            ViewBag.MeterDispatched = await _db.MeterDispatches
                .AsNoTracking()
                .AnyAsync(m => m.SolarRequestId == dashboard.LatestProject.Id && m.IsDispatched);

            ViewBag.TotalSubmitted = await _paymentService.GetTotalPaidAsync(dashboard.LatestProject.Id) + deposit;
            var verified = await _paymentService.GetVerifiedPaidAsync(dashboard.LatestProject.Id) + deposit;
            ViewBag.VerifiedPaid   = verified;
            ViewBag.Minimum        = Math.Max(0m, PaymentService.MinimumPaymentThreshold - deposit);

            // "ID Active" option (spec): a "Without Activation" ID may activate as
            // soon as the admin has VERIFIED the ₹20,000 minimum — it no longer
            // waits for the plan to be paid in full. Same helper as the Status page
            // so the two screens cannot disagree.
            var lp = dashboard.LatestProject;
            ViewBag.CanActivateNow =
                WorkflowGates.IsActivationEligible(lp.RequestType, lp.RequestedAmount, verified);
            ViewBag.ActivationOutstanding =
                WorkflowGates.OutstandingAmount(lp.RequestedAmount, verified);
        }
        return View(dashboard);
    }
}
