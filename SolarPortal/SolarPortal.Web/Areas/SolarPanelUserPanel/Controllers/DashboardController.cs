using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Controllers;

[Area("SolarPanelUserPanel")]
[Authorize(Roles = "User")]
public class DashboardController : Controller
{
    private readonly IDashboardService _dashboardService;
    private readonly IPaymentService _paymentService;
    private readonly UserManager<ApplicationUser> _userManager;

    public DashboardController(
        IDashboardService dashboardService,
        IPaymentService paymentService,
        UserManager<ApplicationUser> userManager)
    {
        _dashboardService = dashboardService;
        _paymentService = paymentService;
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
            ViewBag.TotalSubmitted = await _paymentService.GetTotalPaidAsync(dashboard.LatestProject.Id);
            ViewBag.VerifiedPaid   = await _paymentService.GetVerifiedPaidAsync(dashboard.LatestProject.Id);
            ViewBag.Minimum        = PaymentService.MinimumPaymentThreshold;
        }
        return View(dashboard);
    }
}
