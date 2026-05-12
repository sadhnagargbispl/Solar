using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Web.Areas.User.Controllers;

[Area("User")]
[Authorize(Roles = "User")]
public class AccountController : Controller
{
    private readonly ISolarRequestService _solarRequestService;
    private readonly IPaymentService _paymentService;
    private readonly UserManager<ApplicationUser> _userManager;

    public AccountController(
        ISolarRequestService solarRequestService,
        IPaymentService paymentService,
        UserManager<ApplicationUser> userManager)
    {
        _solarRequestService = solarRequestService;
        _paymentService = paymentService;
        _userManager = userManager;
    }

    public async Task<IActionResult> SolarAccount()
    {
        var userId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.GetByUserIdAsync(userId);
        var projects = result.Data?.ToList() ?? new List<SolarRequestDto>();

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
