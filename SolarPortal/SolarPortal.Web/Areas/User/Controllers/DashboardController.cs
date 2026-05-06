using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Web.Areas.User.Controllers;

[Area("User")]
[Authorize(Roles = "User")]
public class DashboardController : Controller
{
    private readonly IDashboardService _dashboardService;
    private readonly UserManager<ApplicationUser> _userManager;

    public DashboardController(IDashboardService dashboardService, UserManager<ApplicationUser> userManager)
    {
        _dashboardService = dashboardService;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var dashboard = await _dashboardService.GetUserDashboardAsync(userId);
        return View(dashboard);
    }
}