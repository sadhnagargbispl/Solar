using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.Areas.SolarPanelInstaller.Controllers;

/// <summary>
/// INC panel — KYC upload.
///
/// Image point 8 (INC panel → admin): "INC — commission wale ka KYC upload system
/// banana hai, approve by admin. JOB wale ka KYC nahi lena hai."
///
/// The page is modelled on the existing member-panel KYC screen (legacy KYC.aspx):
/// three sections — Address Proof, Bank Detail, PAN Card — each saved and verified
/// on its own. Masters come from the same legacy tables the old page used
/// (M_IdTypeMaster, M_BankMaster, M_StateDivMaster) so the lists match exactly.
///
/// Approval itself happens in the ADMIN app (separate app, shared DB), which sets
/// the per-section status + remark. A rejected section unlocks here so the
/// installer can correct it, and saving it puts that section back to Pending.
/// </summary>
[Area("SolarPanelInstaller")]
[Authorize(Roles = "Installer")]
public class KycController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IFileUploadService _fileUpload;
    private readonly IStateService _states;
    private readonly IBankService _banks;
    private readonly IIdProofTypeService _idProofTypes;

    public KycController(
        IUnitOfWork uow,
        IFileUploadService fileUpload,
        IStateService states,
        IBankService banks,
        IIdProofTypeService idProofTypes)
    {
        _uow = uow;
        _fileUpload = fileUpload;
        _states = states;
        _banks = banks;
        _idProofTypes = idProofTypes;
    }

    private int WorkerId => int.TryParse(User.FindFirst("WorkerId")?.Value, out var id) ? id : 0;

    /// <summary>
    /// INC-only gate. The WorkerType claim is stamped into the auth cookie at
    /// login, so a worker whose type admin changed mid-session would carry a stale
    /// answer — the type is therefore re-read from the Workers table, exactly like
    /// IncWalletService does before it pays anyone.
    /// </summary>
    private async Task<Worker?> GetIncWorkerAsync()
    {
        var wid = WorkerId;
        if (wid <= 0) return null;
        var worker = await _uow.Workers.GetByIdAsync(wid);
        return worker != null && worker.Type == WorkerType.INC ? worker : null;
    }

    private async Task<IncKycDocument?> GetKycAsync(int workerId) =>
        (await _uow.IncKycDocuments.FindAsync(k => k.WorkerId == workerId))
        .OrderByDescending(k => k.Id)
        .FirstOrDefault();

    /// <summary>Loads the row, creating an empty one the first time the page opens.</summary>
    private async Task<IncKycDocument> GetOrCreateKycAsync(int workerId)
    {
        var kyc = await GetKycAsync(workerId);
        if (kyc != null) return kyc;

        kyc = new IncKycDocument { WorkerId = workerId, CreatedAt = DateTime.UtcNow };
        await _uow.IncKycDocuments.AddAsync(kyc);
        await _uow.SaveChangesAsync();
        return kyc;
    }

    private async Task LoadMastersAsync()
    {
        ViewBag.States = await _states.GetActiveAsync();
        ViewBag.Banks = await _banks.GetActiveAsync();
        ViewBag.IdProofTypes = await _idProofTypes.GetActiveAsync();
    }

    // GET: /SolarPanelInstaller/Kyc
    public async Task<IActionResult> Index()
    {
        var worker = await GetIncWorkerAsync();
        if (worker == null)
        {
            TempData["Info"] = "KYC is collected only from INC (commission) installers.";
            return RedirectToAction("Index", "Dashboard");
        }

        var kyc = await GetOrCreateKycAsync(worker.Id);
        await LoadMastersAsync();
        ViewBag.Worker = worker;
        return View(kyc);
    }

    // POST: /SolarPanelInstaller/Kyc/SaveAddress
    // Section 1 — Address Proof (front + back image, like the legacy page).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAddress(
        string? address, string? pinCode, string? stateCode, string? district, string? city,
        int? idProofTypeId, string? idProofNo,
        IFormFile? frontProof, IFormFile? backProof)
    {
        var worker = await GetIncWorkerAsync();
        if (worker == null)
        {
            TempData["Info"] = "KYC is collected only from INC (commission) installers.";
            return RedirectToAction("Index", "Dashboard");
        }

        var kyc = await GetOrCreateKycAsync(worker.Id);
        if (!kyc.AddressEditable)
        {
            TempData["Warning"] = "Address proof is already submitted and cannot be changed.";
            return RedirectToAction(nameof(Index));
        }

        // Mandatory on the first submission; on a correction the installer may keep
        // the file that was already accepted and only fix the typed details.
        var hasFront = frontProof is { Length: > 0 } || !string.IsNullOrWhiteSpace(kyc.AddressProofFrontPath);
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(pinCode) ||
            !idProofTypeId.HasValue || string.IsNullOrWhiteSpace(idProofNo) || !hasFront)
        {
            TempData["Warning"] = "Address, pincode, ID proof type, ID proof number and the front image are all required.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            kyc.Address   = address.Trim();
            kyc.PinCode   = pinCode.Trim();
            kyc.District  = district?.Trim();
            kyc.City      = city?.Trim();
            kyc.IdProofNo = idProofNo.Trim().ToUpperInvariant();

            // Store the display name alongside the code so admin reports don't have
            // to re-join the legacy masters.
            if (!string.IsNullOrWhiteSpace(stateCode))
            {
                kyc.StateCode = stateCode.Trim();
                kyc.StateName = (await _states.GetActiveAsync())
                    .FirstOrDefault(s => s.StateCode == kyc.StateCode)?.StateName;
            }
            kyc.IdProofTypeId = idProofTypeId;
            kyc.IdProofTypeName = (await _idProofTypes.GetActiveAsync())
                .FirstOrDefault(t => t.Id == idProofTypeId)?.IdType;

            var front = await SaveFileAsync(frontProof, worker.Id, "address-front");
            if (front != null) kyc.AddressProofFrontPath = front;
            var back = await SaveFileAsync(backProof, worker.Id, "address-back");
            if (back != null) kyc.AddressProofBackPath = back;

            ResetSection(ref kyc, "address");
            await SaveAsync(kyc, worker.Id);
            TempData["Success"] = "Address proof submitted. Admin will verify it shortly.";
        }
        catch (Exception ex)
        {
            TempData["Warning"] = $"Could not save address proof: {ex.InnerException?.Message ?? ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    // POST: /SolarPanelInstaller/Kyc/SaveBank
    // Section 2 — Bank Detail. Mirrors the legacy page's SaveButton() checks.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBank(
        string? accountType, string? accountNo, int? bankId, string? branchName, string? ifscCode,
        IFormFile? bankProof)
    {
        var worker = await GetIncWorkerAsync();
        if (worker == null)
        {
            TempData["Info"] = "KYC is collected only from INC (commission) installers.";
            return RedirectToAction("Index", "Dashboard");
        }

        var kyc = await GetOrCreateKycAsync(worker.Id);
        if (!kyc.BankEditable)
        {
            TempData["Warning"] = "Bank detail is already submitted and cannot be changed.";
            return RedirectToAction(nameof(Index));
        }

        var hasProof = bankProof is { Length: > 0 } || !string.IsNullOrWhiteSpace(kyc.BankProofPath);
        if (string.IsNullOrWhiteSpace(accountNo) || !bankId.HasValue || bankId <= 0 ||
            string.IsNullOrWhiteSpace(branchName) || string.IsNullOrWhiteSpace(ifscCode) || !hasProof)
        {
            TempData["Warning"] = "Account number, bank, branch, IFSC code and the passbook / cancelled-cheque image are all required.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            kyc.AccountType = accountType;
            kyc.AccountNo   = accountNo.Trim();
            kyc.BankId      = bankId;
            kyc.BankName    = (await _banks.GetActiveAsync())
                .FirstOrDefault(b => b.BId == bankId)?.BankName;
            kyc.BranchName  = branchName.Trim();
            kyc.IfscCode    = ifscCode.Trim().ToUpperInvariant();

            var proof = await SaveFileAsync(bankProof, worker.Id, "bank");
            if (proof != null) kyc.BankProofPath = proof;

            ResetSection(ref kyc, "bank");
            await SaveAsync(kyc, worker.Id);
            TempData["Success"] = "Bank detail submitted. Admin will verify it shortly.";
        }
        catch (Exception ex)
        {
            TempData["Warning"] = $"Could not save bank detail: {ex.InnerException?.Message ?? ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    // POST: /SolarPanelInstaller/Kyc/SavePan
    // Section 3 — PAN Card.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePan(string? panNo, IFormFile? panProof)
    {
        var worker = await GetIncWorkerAsync();
        if (worker == null)
        {
            TempData["Info"] = "KYC is collected only from INC (commission) installers.";
            return RedirectToAction("Index", "Dashboard");
        }

        var kyc = await GetOrCreateKycAsync(worker.Id);
        if (!kyc.PanEditable)
        {
            TempData["Warning"] = "PAN card is already submitted and cannot be changed.";
            return RedirectToAction(nameof(Index));
        }

        var hasProof = panProof is { Length: > 0 } || !string.IsNullOrWhiteSpace(kyc.PanProofPath);
        if (string.IsNullOrWhiteSpace(panNo) || !hasProof)
        {
            TempData["Warning"] = "PAN number and the PAN card image are both required.";
            return RedirectToAction(nameof(Index));
        }

        var pan = panNo.Trim().ToUpperInvariant();
        // Same format the legacy page validates with custom[panno].
        if (!System.Text.RegularExpressions.Regex.IsMatch(pan, "^[A-Z]{5}[0-9]{4}[A-Z]$"))
        {
            TempData["Warning"] = "PAN number must look like ABCDE1234F.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            kyc.PanNo = pan;
            var proof = await SaveFileAsync(panProof, worker.Id, "pan");
            if (proof != null) kyc.PanProofPath = proof;

            ResetSection(ref kyc, "pan");
            await SaveAsync(kyc, worker.Id);
            TempData["Success"] = "PAN card submitted. Admin will verify it shortly.";
        }
        catch (Exception ex)
        {
            TempData["Warning"] = $"Could not save PAN card: {ex.InnerException?.Message ?? ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>Uploads one file to uploads/inc-kyc/&lt;workerId&gt;/&lt;slot&gt;/, or null if nothing was posted.</summary>
    private async Task<string?> SaveFileAsync(IFormFile? file, int workerId, string slot)
    {
        if (file == null || file.Length == 0) return null;
        var (ok, path, _) = await _fileUpload.UploadAsync(file, $"inc-kyc/{workerId}/{slot}");
        return ok ? path : null;
    }

    /// <summary>
    /// A freshly saved section always goes back to Pending with its old reject
    /// remark cleared — admin reviews the new submission on its own merits.
    /// </summary>
    private static void ResetSection(ref IncKycDocument kyc, string section)
    {
        switch (section)
        {
            case "address":
                kyc.AddressStatus = ApprovalStatus.Pending;
                kyc.AddressRemark = null;
                kyc.AddressReviewedAt = null;
                kyc.AddressReviewedBy = null;
                break;
            case "bank":
                kyc.BankStatus = ApprovalStatus.Pending;
                kyc.BankRemark = null;
                kyc.BankReviewedAt = null;
                kyc.BankReviewedBy = null;
                break;
            case "pan":
                kyc.PanStatus = ApprovalStatus.Pending;
                kyc.PanRemark = null;
                kyc.PanReviewedAt = null;
                kyc.PanReviewedBy = null;
                break;
        }
    }

    private async Task SaveAsync(IncKycDocument kyc, int workerId)
    {
        kyc.SubmittedAt = DateTime.UtcNow;
        kyc.UpdatedAt = DateTime.UtcNow;
        kyc.UpdatedBy = "worker-" + workerId;
        _uow.IncKycDocuments.Update(kyc);
        await _uow.SaveChangesAsync();
    }
}
