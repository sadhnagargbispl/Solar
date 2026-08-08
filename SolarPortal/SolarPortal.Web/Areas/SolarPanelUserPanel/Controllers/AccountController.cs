using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using static SolarPortal.Web.Areas.SolarPanelUserPanel.Helpers.MemberIdNo;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Controllers;

[Area("SolarPanelUserPanel")]
[Authorize(Roles = "User")]
public class AccountController : Controller
{
    private readonly ISolarRequestService _solarRequestService;
    private readonly IPaymentService _paymentService;
    private readonly ILegacyProductRequestService _legacyBridge;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _config;

    public AccountController(
        ISolarRequestService solarRequestService,
        IPaymentService paymentService,
        ILegacyProductRequestService legacyBridge,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
        _solarRequestService = solarRequestService;
        _paymentService = paymentService;
        _legacyBridge = legacyBridge;
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

        // === Already-Active deposit (image point 1) ===
        // "Agar ID already Active hai to jis order ka amount jama kiya wo Solar
        // Panel par jama dikhe." That money lives in the legacy cPanel as approved
        // TrnProductorderDetail rows — it is a real credit against the project, so
        // it counts towards Total Paid and shrinks the Due here, exactly as it does
        // on the request / payment forms.
        //
        // Only "Already Active" requests draw on it; every other mode gets 0 and
        // the totals below stay exactly what they always were.
        // Resolve through the SHARED rule, keyed off the request that actually
        // carries the Already-Active mode - not off the signed-in user id. The two
        // are normally the same, but this page was computing its own answer and
        // silently reporting 0, which is how Solar A/c ended up billing a member
        // for money they had already paid.
        decimal activeDeposit = 0m;
        var activeReq = projects.FirstOrDefault(p => p.RequestType == RequestType.AlreadyActiveOnlyRequest);
        if (activeReq != null)
        {
            activeDeposit = await _legacyBridge.GetDepositForRequestAsync(
                activeReq.RequestType,
                Normalize(string.IsNullOrWhiteSpace(activeReq.UserId) ? userId : activeReq.UserId));
        }

        // Never let the deposit push Due below zero — a member whose old order was
        // bigger than the solar plan simply owes nothing.
        var creditedTotal = totalPaid + activeDeposit;

        ViewBag.Projects = projects;
        ViewBag.Payments = allPayments;
        ViewBag.TotalProjectAmount = totalProject;
        // Point 1: the deposit is money this project has ALREADY received, so it
        // belongs in Total Paid too - not just netted out of Due. Showing
        // "Paid ₹0" beside "Due ₹26,400" on a ₹30,000 project made the card
        // contradict itself and read like a bug.
        ViewBag.TotalPaid = creditedTotal;
        ViewBag.PaymentsOnly = totalPaid;   // receipts alone, for the sub-line
        ViewBag.ActiveIdDeposit = activeDeposit;
        ViewBag.TotalDue = Math.Max(0m, totalProject - creditedTotal);

        return View();
    }
}
