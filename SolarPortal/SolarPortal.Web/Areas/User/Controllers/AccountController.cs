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

        var paymentTasks = projects.Select(p => _paymentService.GetByRequestIdAsync(p.Id));
        var allPaymentLists = await Task.WhenAll(paymentTasks);
        var allPayments = allPaymentLists.SelectMany(p => p).ToList();

        ViewBag.Projects = projects;
        ViewBag.Payments = allPayments;
        ViewBag.TotalProjectAmount = projects.Sum(p => p.RequestedAmount);
        ViewBag.TotalPaid = allPayments.Where(p => p.IsVerified).Sum(p => p.Amount);
        ViewBag.TotalDue = (decimal)ViewBag.TotalProjectAmount - (decimal)ViewBag.TotalPaid;

        return View();
    }
}
