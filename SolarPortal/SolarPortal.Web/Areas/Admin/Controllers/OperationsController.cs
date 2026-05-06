using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class OperationsController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ISolarRequestService _requestService;
    private readonly IFileUploadService _fileUploadService;
    private readonly UserManager<ApplicationUser> _userManager;

    public OperationsController(IUnitOfWork uow, ISolarRequestService requestService,
        IFileUploadService fileUploadService, UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _requestService = requestService;
        _fileUploadService = fileUploadService;
        _userManager = userManager;
    }

    // --- Meter Dispatch ---
    public async Task<IActionResult> MeterDispatch()
    {
        var requests = await _uow.SolarRequests.FindAsync(
            x => x.CurrentStage == ProjectStatus.PMSurvey &&
                 x.ApprovalStatus == ApprovalStatus.Approved);
        ViewBag.Title = "Meter Dispatch";
        return View("OperationsList", requests);
    }

    [HttpPost]
    public async Task<IActionResult> SubmitMeterDispatch(int requestId, string meterNumber,
        string meterType, IFormFile? dispatchDoc)
    {
        string? docPath = null;
        if (dispatchDoc != null)
        {
            var (ok, path, _) = await _fileUploadService.UploadAsync(dispatchDoc, "dispatch/meter");
            if (ok) docPath = path;
        }

        var dispatch = new MeterDispatch
        {
            SolarRequestId = requestId,
            MeterNumber = meterNumber,
            MeterType = meterType,
            DispatchDate = DateTime.UtcNow,
            DispatchDocumentPath = docPath,
            IsDispatched = true,
            DispatchedBy = _userManager.GetUserId(User)
        };

        await _uow.MeterDispatches.AddAsync(dispatch);
        await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
        {
            Id = requestId,
            NewStage = ProjectStatus.SiteSurvey
        }, _userManager.GetUserId(User)!);

        await _uow.SaveChangesAsync();
        return Json(new { success = true, message = "Meter dispatched successfully" });
    }

    // --- Site Survey ---
    public async Task<IActionResult> SiteSurvey()
    {
        var requests = await _uow.SolarRequests.FindAsync(
            x => x.CurrentStage == ProjectStatus.SiteSurvey);
        ViewBag.Title = "Site Survey";
        return View("OperationsList", requests);
    }

    // --- Material Dispatch ---
    public async Task<IActionResult> MaterialDispatch()
    {
        var requests = await _uow.SolarRequests.FindAsync(
            x => x.CurrentStage == ProjectStatus.SiteSurvey);
        ViewBag.Title = "Material Dispatch";
        return View("OperationsList", requests);
    }

    // --- Installation ---
    public async Task<IActionResult> Installation()
    {
        var requests = await _uow.SolarRequests.FindAsync(
            x => x.CurrentStage == ProjectStatus.MaterialDispatch ||
                 x.CurrentStage == ProjectStatus.Installation);
        ViewBag.Title = "Installation";

        var workers = await _uow.Workers.FindAsync(w => w.IsAvailable);
        ViewBag.Workers = workers;
        return View("OperationsList", requests);
    }

    // --- DCR Update ---
    public async Task<IActionResult> DCRUpdate()
    {
        var requests = await _uow.SolarRequests.FindAsync(
            x => x.CurrentStage == ProjectStatus.Installation &&
                 x.ConnectionType == ConnectionType.Domestic);
        ViewBag.Title = "DCR Update";
        return View("OperationsList", requests);
    }

    [HttpPost]
    public async Task<IActionResult> SubmitDCR(int requestId, string dcrNumber,
        IFormFile? dcrDoc)
    {
        string? docPath = null;
        if (dcrDoc != null)
        {
            var (ok, path, _) = await _fileUploadService.UploadAsync(dcrDoc, "dcr");
            if (ok) docPath = path;
        }

        var dcr = new DCRDocument
        {
            SolarRequestId = requestId,
            DCRNumber = dcrNumber,
            DCRDate = DateTime.UtcNow,
            DocumentPath = docPath,
            ExtractedData = SimulateOCR(dcrNumber),
            IsVerified = true
        };

        await _uow.DCRDocuments.AddAsync(dcr);
        await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
        {
            Id = requestId,
            NewStage = ProjectStatus.Completed
        }, _userManager.GetUserId(User)!);

        await _uow.SaveChangesAsync();
        return Json(new { success = true, message = "DCR submitted. Project completed!" });
    }

    private static string SimulateOCR(string dcrNumber) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            DCRNumber = dcrNumber,
            ExtractedDate = DateTime.Today.ToString("dd/MM/yyyy"),
            Status = "Verified",
            Confidence = "98%"
        });
}