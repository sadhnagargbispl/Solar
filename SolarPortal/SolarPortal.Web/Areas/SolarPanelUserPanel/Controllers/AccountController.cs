using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Controllers;

[Area("SolarPanelUserPanel")]
[Authorize(Roles = "User")]
public class AccountController : Controller
{
    private readonly ISolarRequestService _solarRequestService;
    private readonly IPaymentService _paymentService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _config;

    public AccountController(
        ISolarRequestService solarRequestService,
        IPaymentService paymentService,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
        _solarRequestService = solarRequestService;
        _paymentService = paymentService;
        _userManager = userManager;
        _config = config;
    }

    public async Task<IActionResult> SolarAccount()
    {
        var userId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.GetByUserIdAsync(userId);
        // Per spec: jab tak user request submit na kare, Solar Account pe koi
        // record nahi dikhna chahiye. Filter out the unfilled auto-stub (no plan/
        // product picked, KV 0, amount 0, not completed) — same rule used on the
        // Dashboard / My Projects / Status pages.
        var projects = (result.Data ?? Enumerable.Empty<SolarRequestDto>())
                        .Where(p => !(
                            !p.SolarProjectId.HasValue &&
                            !p.ExternalProductId.HasValue &&
                            p.KVCapacity == 0m &&
                            p.PlanAmount == 0m &&
                            p.RequestedAmount == 0m &&
                            p.CurrentStage != ProjectStatus.Completed))
                        .ToList();

        // === Mode 2 redirect rule ===
        // If the user's most-recent request was created under "Only Solar — Without Activation"
        // mode, route them to the external main portal where the actual solar summary lives.
        // Configured via appsettings.json → ExternalRedirects:Mode2SolarSummaryUrl.
        var latest = projects.OrderByDescending(p => p.CreatedAt).FirstOrDefault();
        if (latest != null && latest.RequestType == RequestType.OnlySolarWithoutActivation)
        {
            var url = _config["ExternalRedirects:Mode2SolarSummaryUrl"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                ViewBag.RedirectUrl = url;
                ViewBag.RedirectMessage =
                    "Since your request was created under 'Only Solar — Without Activation', " +
                    "your full Solar Summary is hosted on our main portal. Redirecting you now…";
                return View("ExternalRedirect");
            }
        }

        // Run sequentially — EF Core forbids concurrent ops on the same DbContext
        var allPayments = new List<PaymentDto>();
        foreach (var p in projects)
        {
            var pays = await _paymentService.GetByRequestIdAsync(p.Id);
            allPayments.AddRange(pays);
        }

        var totalProject = projects.Sum(p => p.RequestedAmount);
        var totalPaid = allPayments.Where(p => p.IsVerified).Sum(p => p.Amount);

        ViewBag.Projects = projects;
        ViewBag.Payments = allPayments;
        ViewBag.TotalProjectAmount = totalProject;
        ViewBag.TotalPaid = totalPaid;
        ViewBag.TotalDue = totalProject - totalPaid;

        return View();
    }
}
