using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Web.Models;

namespace SolarPortal.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    // "/" must land on the signed-in user's own dashboard. Each panel lives in
    // its own area, so the role picks the dashboard. Without this the root URL
    // served the scaffolded welcome view - sidebar rendered, but no content.
    // There is no public landing page in this portal, so a signed-out visitor
    // goes to login and comes back here afterwards.
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated != true)
            return RedirectToAction("Login", "Account");

        if (User.IsInRole("Installer"))
            return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelInstaller" });

        return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelUserPanel" });
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
