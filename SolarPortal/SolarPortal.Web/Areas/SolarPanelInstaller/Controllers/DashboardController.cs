using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SolarPortal.Web.Areas.SolarPanelInstaller.Controllers;

// Installer / INC panel. Accessible only to users in the "Installer" role.
// Login is unified through the user-panel login page (Account/Login) which
// routes Installer users here.
[Area("SolarPanelInstaller")]
[Authorize(Roles = "Installer")]
public class DashboardController : Controller
{
    public IActionResult Index() => View();
}