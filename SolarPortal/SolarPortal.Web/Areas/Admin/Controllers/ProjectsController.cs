using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class ProjectsController : Controller
{
    private readonly ISolarRequestService _solarRequestService;
    private readonly UserManager<ApplicationUser> _userManager;

    public ProjectsController(ISolarRequestService solarRequestService, UserManager<ApplicationUser> userManager)
    {
        _solarRequestService = solarRequestService;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var result = await _solarRequestService.GetAllAsync();
        return View(result.Data ?? Enumerable.Empty<SolarRequestDto>());
    }

    public async Task<IActionResult> Details(int id)
    {
        var result = await _solarRequestService.GetWithDetailsAsync(id);
        if (!result.IsSuccess) return NotFound();
        return View(result.Data);
    }

    public async Task<IActionResult> Approvals()
    {
        var result = await _solarRequestService.GetPendingApprovalsAsync();
        return View(result.Data ?? Enumerable.Empty<SolarRequestDto>());
    }

    [HttpPost]
    public async Task<IActionResult> Approve(int id, string? notes)
    {
        var adminId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.ApproveAsync(id, adminId, notes);
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }

    [HttpPost]
    public async Task<IActionResult> Reject(int id, string reason)
    {
        var adminId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.RejectAsync(id, adminId, reason);
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }

    [HttpPost]
    public async Task<IActionResult> UpdateStage(UpdateSolarRequestStatusDto dto)
    {
        var adminId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.UpdateStageAsync(dto, adminId);
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }
}