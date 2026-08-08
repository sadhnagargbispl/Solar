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
    private readonly UserManager<ApplicationUser> _userManager;

    public DashboardController(
        IDashboardService dashboardService,
        IPaymentService paymentService,
        ILegacyProductRequestService deposits,
        UserManager<ApplicationUser> userManager)
    {
        _dashboardService = dashboardService;
        _paymentService = paymentService;
        _deposits = deposits;
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

            ViewBag.TotalSubmitted = await _paymentService.GetTotalPaidAsync(dashboard.LatestProject.Id) + deposit;
            var verified = await _paymentService.GetVerifiedPaidAsync(dashboard.LatestProject.Id) + deposit;
            ViewBag.VerifiedPaid   = verified;
            ViewBag.Minimum        = Math.Max(0m, PaymentService.MinimumPaymentThreshold - deposit);

            // "Activate Now" — only for a fully-paid "Without Activation" ID (spec).
            var lp = dashboard.LatestProject;
            ViewBag.CanActivateNow =
                lp.RequestType == SolarPortal.Domain.Enums.RequestType.OnlySolarWithoutActivation
                && lp.RequestedAmount > 0
                && verified >= lp.RequestedAmount;
        }
        return View(dashboard);
    }
}
