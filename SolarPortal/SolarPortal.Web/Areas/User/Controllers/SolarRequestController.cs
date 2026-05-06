using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Web.ViewModels;

namespace SolarPortal.Web.Areas.User.Controllers;

[Area("User")]
[Authorize(Roles = "User")]
public class SolarRequestController : Controller
{
    private readonly ISolarRequestService _solarRequestService;
    private readonly IPaymentService _paymentService;
    private readonly IDocumentService _documentService;
    private readonly IFileUploadService _fileUploadService;
    private readonly UserManager<ApplicationUser> _userManager;

    public SolarRequestController(
        ISolarRequestService solarRequestService,
        IPaymentService paymentService,
        IDocumentService documentService,
        IFileUploadService fileUploadService,
        UserManager<ApplicationUser> userManager)
    {
        _solarRequestService = solarRequestService;
        _paymentService = paymentService;
        _documentService = documentService;
        _fileUploadService = fileUploadService;
        _userManager = userManager;
    }

    // GET: New Request Form (multi-step)
    public IActionResult Create() => View(new CreateSolarRequestViewModel());

    // POST: Step 1 - Personal Info
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateSolarRequestViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var userId = _userManager.GetUserId(User)!;
        var dto = new CreateSolarRequestDto
        {
            ApplicantName = model.ApplicantName,
            MobileNumber = model.MobileNumber,
            Email = model.Email,
            Address = model.Address,
            City = model.City,
            State = model.State,
            PinCode = model.PinCode,
            AadharNumber = model.AadharNumber,
            PANNumber = model.PANNumber,
            ConnectionType = model.ConnectionType,
            KVCapacity = model.KVCapacity,
            SelectedPlan = model.SelectedPlan,
            PlanAmount = model.PlanAmount
        };

        var result = await _solarRequestService.CreateAsync(dto, userId);
        if (!result.IsSuccess)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error);
            return View(model);
        }

        TempData["Success"] = $"Request {result.Data!.RequestNumber} submitted successfully!";
        TempData["RequestId"] = result.Data.Id;
        return RedirectToAction("UploadDocuments", new { id = result.Data.Id });
    }

    // GET: Upload Documents
    public async Task<IActionResult> UploadDocuments(int id)
    {
        var result = await _solarRequestService.GetByIdAsync(id);
        if (!result.IsSuccess) return NotFound();
        ViewBag.RequestId = id;
        ViewBag.RequestNumber = result.Data!.RequestNumber;
        return View();
    }

    // POST: Upload Document (AJAX)
    [HttpPost]
    public async Task<IActionResult> UploadDocument(int requestId, string documentType, IFormFile file)
    {
        if (file == null)
            return Json(new { success = false, message = "No file provided" });

        var (success, filePath, error) = await _fileUploadService.UploadAsync(file, $"documents/{requestId}");
        if (!success)
            return Json(new { success = false, message = error });

        var userId = _userManager.GetUserId(User)!;
        await _documentService.SaveDocumentAsync(new SaveDocumentDto
        {
            SolarRequestId = requestId,
            UserId = userId,
            DocumentType = Enum.Parse<Domain.Enums.DocumentType>(documentType),
            FilePath = filePath!,
            FileName = Path.GetFileNameWithoutExtension(file.FileName),
            OriginalFileName = file.FileName,
            FileSizeBytes = file.Length,
            ContentType = file.ContentType
        });

        return Json(new { success = true, filePath, message = "Document uploaded successfully" });
    }

    // GET: My Projects list
    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.GetByUserIdAsync(userId);
        return View(result.Data ?? Enumerable.Empty<SolarRequestDto>());
    }

    // GET: Project detail + status flow
    public async Task<IActionResult> Details(int id)
    {
        var result = await _solarRequestService.GetWithDetailsAsync(id);
        if (!result.IsSuccess) return NotFound();
        return View(result.Data);
    }

    // GET: Payment page
    public async Task<IActionResult> Payment(int id)
    {
        var result = await _solarRequestService.GetByIdAsync(id);
        if (!result.IsSuccess) return NotFound();
        ViewBag.Payments = await _paymentService.GetByRequestIdAsync(id);
        return View(result.Data);
    }

    // POST: Add Payment (AJAX)
    [HttpPost]
    public async Task<IActionResult> AddPayment(CreatePaymentDto dto, IFormFile? receiptImage)
    {
        var userId = _userManager.GetUserId(User)!;
        dto.UserId = userId;

        if (receiptImage != null)
        {
            var (success, path, error) = await _fileUploadService.UploadAsync(receiptImage, "payments");
            if (success) dto.ReceiptImagePath = path;
        }

        var result = await _paymentService.CreateAsync(dto);
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }

    // GET: Status tracker
    public async Task<IActionResult> Status(int? id)
    {
        var userId = _userManager.GetUserId(User)!;
        if (id.HasValue)
        {
            var result = await _solarRequestService.GetWithDetailsAsync(id.Value);
            if (result.IsSuccess) return View(result.Data);
        }
        // Show latest project by default
        var projects = await _solarRequestService.GetByUserIdAsync(userId);
        var latest = projects.Data?.FirstOrDefault();
        return View(latest);
    }

    // AJAX: Get status flow JSON
    [HttpGet]
    public async Task<IActionResult> GetStatusFlow(int id)
    {
        var result = await _solarRequestService.GetWithDetailsAsync(id);
        if (!result.IsSuccess)
            return Json(new { success = false });

        var project = result.Data!;
        return Json(new
        {
            success = true,
            currentStage = (int)project.CurrentStage,
            approvalStatus = project.ApprovalStatus.ToString(),
            requestNumber = project.RequestNumber
        });
    }
}