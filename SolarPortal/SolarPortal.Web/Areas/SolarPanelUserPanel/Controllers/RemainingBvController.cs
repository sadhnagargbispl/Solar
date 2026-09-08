using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;
using SolarPortal.Web.Areas.SolarPanelUserPanel.Helpers;
using SolarPortal.Web.ViewModels;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Controllers;

/// <summary>
/// "Update Remaining BV" - the member picks who receives the BV left over after
/// the solar request has taken its fixed share, plus the read-only report.
///
/// Spec:
///   * The page opens only AFTER the installation is complete -
///     "installation complete hone ke baad remaining BV le sakta hai, baaki nahi
///     le sakta." An approved solar request alone is not enough; see
///     WorkflowGates.IsInstallationComplete.
///   * AND only after the plan is PAID IN FULL - "full paid amount hone ke baad hi
///     remaining BV update kar sakta hai." Only admin-verified payments (plus the
///     already-active cPanel deposit) count; see OutstandingAmountAsync.
///   * Sponsor ID    - automatic, the member's sponsor.
///   * Discom Income - Self / Other. "Other" is verified to sit ABOVE the member
///                     in the sponsor tree, never below.
///   * Deal Close    - same rule.
///   * SCI Income    - always the sponsor's ID.
///   * Each head also shows the money it pays, from the SolarProjects master.
///     Display only - the member picks WHO, never how much.
///   * Saved -> admin verifies. While it sits pending the member cannot change it
///     again. Admin may correct and approve, or reject with a reason. A rejected
///     record comes back here editable and saves onto the SAME row, so
///     re-submitting never creates a duplicate.
///   * After final approval nothing can change.
///
/// Every read-only field is re-resolved server-side on POST, so a hand-edited
/// form can only ever change the two beneficiary choices.
/// </summary>
[Area("SolarPanelUserPanel")]
[Authorize(Roles = "User")]
public class RemainingBvController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ISponsorTreeService _sponsorTree;
    private readonly ILegacyProductRequestService _legacyOrders;
    private readonly INotificationService _notifications;
    private readonly UserManager<ApplicationUser> _userManager;

    /// <summary>
    /// Shown wherever the installation-complete gate turns the member away, so the
    /// GET and the POST always say the same thing.
    /// </summary>
    private const string InstallationPendingMessage =
        "Update Remaining BV opens only after your installation is complete. Once the installer marks it done and the admin approves it, the remaining BV can be distributed.";

    /// <summary>
    /// Shown wherever the full-payment gate turns the member away. Built with the
    /// outstanding amount so the GET and the POST say the same thing.
    /// </summary>
    private static string PaymentPendingMessage(decimal due) =>
        $"Update Remaining BV opens only after the plan is paid in full. ₹{due:N0} is still outstanding (admin-verified payments only) — clear it and this page will open.";

    public RemainingBvController(
        ApplicationDbContext db,
        ISponsorTreeService sponsorTree,
        ILegacyProductRequestService legacyOrders,
        INotificationService notifications,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _sponsorTree = sponsorTree;
        _legacyOrders = legacyOrders;
        _notifications = notifications;
        _userManager = userManager;
    }

    // GET: /User/RemainingBv           -> the member's latest eligible request
    // GET: /User/RemainingBv/Index/5   -> request 5
    public async Task<IActionResult> Index(int? id)
    {
        var req = await ResolveEligibleRequestAsync(id);
        if (req == null)
        {
            TempData["Info"] = "Update Remaining BV opens once the admin approves your solar request.";
            return RedirectToAction("Status", "SolarRequest");
        }

        // "Full paid amount hone ke baad hi remaining BV update kar sakta hai."
        // Money first, then the installation - a member who still owes on the plan
        // is told about the balance, which is the thing they can act on.
        var due = await OutstandingAmountAsync(req);
        if (due > 0m)
        {
            TempData["Info"] = PaymentPendingMessage(due);
            return RedirectToAction("Payment", "SolarRequest", new { id = req.Id });
        }

        if (!WorkflowGates.IsInstallationComplete(req))
        {
            TempData["Info"] = InstallationPendingMessage;
            return RedirectToAction("Status", "SolarRequest");
        }

        var vm = await BuildViewModelAsync(req);
        if (vm == null)
        {
            TempData["Info"] = "Your product request has not been verified yet.";
            return RedirectToAction("Status", "SolarRequest");
        }

        return View(vm);
    }

    // POST: /User/RemainingBv/Save
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int requestId,
        BvBeneficiaryMode DiscomIncomeMode,
        string? DiscomIncomeIdNo,
        BvBeneficiaryMode DealCloseMode,
        string? DealCloseIdNo)
    {
        var req = await ResolveEligibleRequestAsync(requestId);
        if (req == null) return NotFound();

        // The GET already hides the form, so these only catch a hand-posted form, a
        // payment reversed by the admin, or an installation sent back for review in
        // between.
        var due = await OutstandingAmountAsync(req);
        if (due > 0m)
        {
            TempData["Error"] = PaymentPendingMessage(due);
            return RedirectToAction("Payment", "SolarRequest", new { id = req.Id });
        }

        if (!WorkflowGates.IsInstallationComplete(req))
        {
            TempData["Error"] = InstallationPendingMessage;
            return RedirectToAction("Status", "SolarRequest");
        }

        var existing = await _db.RemainingBvUpdates
            .FirstOrDefaultAsync(x => x.SolarRequestId == req.Id);

        // Once submitted the row belongs to the admin until they respond: Approved
        // is final, and Pending is mid-verification, so neither may be re-saved.
        // A REJECTED row is deliberately still editable - that is the whole point
        // of rejection, and it is the same row, so no duplicate is created.
        if (existing != null && existing.Status != ApprovalStatus.Rejected)
        {
            TempData["Error"] = existing.Status == ApprovalStatus.Approved
                ? "This record has been given final approval and can no longer be changed."
                : "This record is with the admin for verification and can no longer be changed.";
            return RedirectToAction(nameof(Index), new { id = req.Id });
        }

        var vm = await BuildViewModelAsync(req, existing);
        if (vm == null) return NotFound();

        // Carry the member's choices back so a validation failure re-renders the
        // form as they filled it, not as it was loaded.
        vm.DiscomIncomeMode = DiscomIncomeMode;
        vm.DiscomIncomeIdNo = DiscomIncomeIdNo?.Trim();
        vm.DealCloseMode = DealCloseMode;
        vm.DealCloseIdNo = DealCloseIdNo?.Trim();

        var discom = await ResolveHeadAsync(vm, DiscomIncomeMode, DiscomIncomeIdNo, "Discom Income");
        var dealClose = await ResolveHeadAsync(vm, DealCloseMode, DealCloseIdNo, "Deal Close");

        // SCI Income is paid to the sponsor, so without one there is nothing
        // meaningful to save - better to stop than to write a blank.
        if (string.IsNullOrWhiteSpace(vm.SponsorIdNo))
            ModelState.AddModelError(string.Empty,
                "No sponsor record was found for your ID. SCI Income can only be paid to the sponsor's ID, so please contact the admin.");

        if (vm.RemainingBV <= 0m)
            ModelState.AddModelError(string.Empty,
                "There is no remaining BV on this solar request to distribute.");

        if (!ModelState.IsValid)
            return View(nameof(Index), vm);

        var row = existing ?? new RemainingBvUpdate { SolarRequestId = req.Id };

        row.RequestNumber = vm.RequestNumber;
        row.MemberIdNo = vm.MemberIdNo;
        row.MemberName = vm.MemberName;
        row.MemberFormNo = vm.MemberFormNo;
        row.SponsorIdNo = vm.SponsorIdNo;
        row.SponsorName = vm.SponsorName;
        row.PlanName = vm.PlanName;
        row.SolarTypeKV = vm.SolarTypeKV;
        row.OrderNo = vm.OrderNo;
        row.ProductName = vm.ProductName;

        row.TotalBV = vm.TotalBV;
        row.FixedBV = vm.FixedBV;
        row.RemainingBV = vm.RemainingBV;
        row.BvSource = vm.BvSource;

        row.DiscomIncomeMode = DiscomIncomeMode;
        row.DiscomIncomeIdNo = discom.IdNo;
        row.DiscomIncomeName = discom.Name;
        row.DiscomIncomeAmount = vm.DiscomIncomeAmount;

        row.DealCloseMode = DealCloseMode;
        row.DealCloseIdNo = dealClose.IdNo;
        row.DealCloseName = dealClose.Name;
        row.DealCloseAmount = vm.DealCloseAmount;

        // "SCI Income - Default Sponsor ka ID save hoga." Never member-editable.
        row.SciIncomeIdNo = vm.SponsorIdNo;
        row.SciIncomeName = vm.SponsorName;
        row.SciIncomeAmount = vm.SciIncomeAmount;

        // Re-submitting after a rejection clears the rejection and puts the row
        // back in the admin's queue.
        row.Status = ApprovalStatus.Pending;
        row.SubmittedAt = DateTime.UtcNow;
        row.RejectionReason = null;
        row.RejectedAt = null;
        row.RejectedBy = null;

        if (existing == null) _db.RemainingBvUpdates.Add(row);
        await _db.SaveChangesAsync();

        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = req.UserId,
            SolarRequestId = req.Id,
            Title = "Remaining BV submitted",
            Message = $"Remaining BV ({row.RemainingBV:N2}) has been sent to the admin for verification.",
            NotificationType = "RemainingBv"
        });

        TempData["Success"] = "Remaining BV record saved. It will be given final approval after the admin verifies it.";
        return RedirectToAction(nameof(Index), new { id = req.Id });
    }

    /// <summary>
    /// AJAX for the "Other" boxes - tells the member, before they submit, whether
    /// the typed IdNo sits above them in the sponsor tree.
    /// </summary>
    // GET: /User/RemainingBv/VerifyId?requestId=5&idNo=SOLFIT1
    [HttpGet]
    public async Task<IActionResult> VerifyId(int requestId, string? idNo)
    {
        var req = await ResolveEligibleRequestAsync(requestId);
        if (req == null || !WorkflowGates.IsInstallationComplete(req)) return NotFound();
        if (await OutstandingAmountAsync(req) > 0m) return NotFound();

        var memberIdNo = MemberIdNo.Normalize(req.UserId);
        var check = await _sponsorTree.CheckRelationAsync(memberIdNo, idNo ?? string.Empty);

        return Json(new
        {
            ok = check.IsAcceptable,
            idNo = check.IdNo,
            name = check.Name,
            level = check.Level,
            relation = check.Relation.ToString(),
            message = check.Message
        });
    }

    // GET: /User/RemainingBv/Report - read only, every record this member has.
    public async Task<IActionResult> Report()
    {
        var userId = _userManager.GetUserId(User)!;
        var memberIdNo = MemberIdNo.Normalize(userId);

        var rows = await _db.RemainingBvUpdates
            .AsNoTracking()
            .Where(x => x.MemberIdNo == memberIdNo)
            // Newest first on the date the report actually shows (Submitted On),
            // not on insert order — a record submitted later but saved earlier was
            // sorting above a newer one. Id only breaks same-timestamp ties.
            .OrderByDescending(x => x.SubmittedAt ?? x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new RemainingBvReportRow
            {
                Id = x.Id,
                RequestNumber = x.RequestNumber ?? string.Empty,
                PlanName = x.PlanName,
                SolarTypeKV = x.SolarTypeKV,
                ProductName = x.ProductName,
                OrderNo = x.OrderNo,
                TotalBV = x.TotalBV,
                FixedBV = x.FixedBV,
                RemainingBV = x.RemainingBV,
                BvSource = x.BvSource,
                SponsorIdNo = x.SponsorIdNo,
                SponsorName = x.SponsorName,
                DiscomIncomeMode = x.DiscomIncomeMode,
                DiscomIncomeIdNo = x.DiscomIncomeIdNo,
                DiscomIncomeName = x.DiscomIncomeName,
                DiscomIncomeAmount = x.DiscomIncomeAmount,
                DealCloseMode = x.DealCloseMode,
                DealCloseIdNo = x.DealCloseIdNo,
                DealCloseName = x.DealCloseName,
                DealCloseAmount = x.DealCloseAmount,
                SciIncomeIdNo = x.SciIncomeIdNo,
                SciIncomeName = x.SciIncomeName,
                SciIncomeAmount = x.SciIncomeAmount,
                Status = x.Status,
                SubmittedAt = x.SubmittedAt,
                ApprovedAt = x.ApprovedAt,
                PayoutPostedAt = x.PayoutPostedAt,
                AdminRemark = x.AdminRemark,
                RejectionReason = x.RejectionReason
            })
            .ToListAsync();

        return View(rows);
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// What is still owed on this request's plan: plan amount minus the money that
    /// counts — admin-VERIFIED payments plus the already-active cPanel deposit,
    /// exactly the pair the Payment / Status / Solar A/c screens add up. Returns 0
    /// when the plan is settled (or carries no amount at all).
    ///
    /// Submitted-but-unverified payments are deliberately excluded: until the admin
    /// verifies them the money is not confirmed, and the whole point of this gate is
    /// that the BV cannot move on unconfirmed money.
    /// </summary>
    private async Task<decimal> OutstandingAmountAsync(SolarRequest req)
    {
        var verified = await _db.Payments
            .AsNoTracking()
            .Where(p => p.SolarRequestId == req.Id && p.IsVerified)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;

        var deposit = await _legacyOrders.GetDepositForRequestAsync(
            req.RequestType, MemberIdNo.Normalize(req.UserId));

        return WorkflowGates.OutstandingAmount(req.PlanAmount, verified + deposit);
    }

    /// <summary>
    /// The member's APPROVED solar request - theirs only, latest first.
    ///
    /// Approval alone does NOT open the page: <see cref="WorkflowGates.IsInstallationComplete"/>
    /// still has to pass, so the installations are loaded with the request.
    /// </summary>
    private async Task<SolarRequest?> ResolveEligibleRequestAsync(int? id)
    {
        var userId = _userManager.GetUserId(User)!;

        var query = _db.SolarRequests
            .AsNoTracking()
            .Include(r => r.SolarProject)
            .Include(r => r.Installations)
            .Where(r => r.UserId == userId && r.ApprovalStatus == ApprovalStatus.Approved);

        if (id is null or 0)
            return await query.OrderByDescending(r => r.Id).FirstOrDefaultAsync();

        return await query.FirstOrDefaultAsync(r => r.Id == id.Value);
    }

    /// <summary>
    /// Builds the screen model.
    ///
    /// The legacy TrnProductorderDetail row is only DEMANDED for a With-Activation
    /// request, because that is the only mode that actually creates one: the member
    /// buys a product, admin verifies it, and IsApprove flips to 'Y'. For
    /// "Only Solar Without Activation" (and for an already-active member whose old
    /// order belongs to a different purchase) no such row is written at all, so
    /// insisting on one left those members stuck on "Your product request has not
    /// been verified yet" forever, even with the installation finished.
    ///
    /// Without an order row the BV simply comes off the plan instead:
    /// RemainingBvCalculator falls back to SolarProjects.BV for the fixed part and
    /// SolarProjects.FinalBV for the total, e.g. 175 - 100 = 75 remaining.
    /// </summary>
    private async Task<RemainingBvViewModel?> BuildViewModelAsync(SolarRequest req, RemainingBvUpdate? existing = null)
    {
        existing ??= await _db.RemainingBvUpdates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SolarRequestId == req.Id);

        var memberIdNo = MemberIdNo.Normalize(req.UserId);
        var member = await _sponsorTree.GetMemberAsync(memberIdNo);

        var order = await _legacyOrders.GetVerifiedOrderAsync(memberIdNo, req.ExternalProductId);
        if (order == null && req.RequestType == RequestType.WithActivation) return null;

        // The project may not be loaded when the request came from the "latest"
        // branch, and it carries both the plan BV and the per-head amounts.
        var project = req.SolarProject
            ?? (req.SolarProjectId.HasValue
                    ? await _db.SolarProjects.AsNoTracking()
                        .FirstOrDefaultAsync(p => p.Id == req.SolarProjectId.Value)
                    : null);

        var bv = RemainingBvCalculator.From(
            orderBV: order?.BV ?? 0m,
            projectBV: project?.BV,
            projectFinalBV: project?.FinalBV,
            projectId: project?.Id,
            discomWork: project?.DiscomWork ?? 0m,
            dealClose: project?.DealClose ?? 0m,
            sczMenue: project?.SCZMenue ?? 0m);

        var vm = new RemainingBvViewModel
        {
            RequestId = req.Id,
            RequestNumber = req.RequestNumber,
            MemberIdNo = member?.IdNo ?? memberIdNo,
            MemberName = member?.FullName ?? req.ApplicantName,
            MemberFormNo = member?.FormNo ?? order?.FormNo ?? 0m,
            SponsorIdNo = member?.SponsorIdNo,
            SponsorName = member?.SponsorName,
            PlanName = project?.Name,
            SolarTypeKV = project?.SolarTypeKV ?? req.KVCapacity,
            OrderNo = order?.OrderNo,
            ProductName = order?.ProductName,
            TotalBV = bv.TotalBV,
            FixedBV = bv.FixedBV,
            RemainingBV = bv.RemainingBV,
            BvSource = bv.Source,
            DiscomIncomeAmount = bv.DiscomIncomeAmount,
            DealCloseAmount = bv.DealCloseAmount,
            SciIncomeAmount = bv.SciIncomeAmount
        };

        // SCI Income always follows the sponsor. On an already-approved row we show
        // what was actually approved, because a later sponsor change must not
        // silently rewrite a frozen payout.
        var approved = existing is { Status: ApprovalStatus.Approved };
        vm.SciIncomeIdNo = approved ? existing!.SciIncomeIdNo : vm.SponsorIdNo;
        vm.SciIncomeName = approved ? existing!.SciIncomeName : vm.SponsorName;

        if (existing != null)
        {
            vm.IsSubmitted = true;
            vm.Status = existing.Status;
            vm.SubmittedAt = existing.SubmittedAt;
            vm.ApprovedAt = existing.ApprovedAt;
            vm.RejectedAt = existing.RejectedAt;
            vm.PayoutPostedAt = existing.PayoutPostedAt;
            vm.AdminRemark = existing.AdminRemark;
            vm.RejectionReason = existing.RejectionReason;

            vm.DiscomIncomeMode = existing.DiscomIncomeMode;
            vm.DiscomIncomeIdNo = existing.DiscomIncomeIdNo;
            vm.DiscomIncomeName = existing.DiscomIncomeName;
            vm.DealCloseMode = existing.DealCloseMode;
            vm.DealCloseIdNo = existing.DealCloseIdNo;
            vm.DealCloseName = existing.DealCloseName;

            // An approved row is a historical record: show exactly what was
            // approved, not what today's masters would compute.
            if (approved)
            {
                vm.TotalBV = existing.TotalBV;
                vm.FixedBV = existing.FixedBV;
                vm.RemainingBV = existing.RemainingBV;
                vm.BvSource = existing.BvSource ?? vm.BvSource;
                vm.SponsorIdNo = existing.SponsorIdNo;
                vm.SponsorName = existing.SponsorName;
                vm.DiscomIncomeAmount = existing.DiscomIncomeAmount;
                vm.DealCloseAmount = existing.DealCloseAmount;
                vm.SciIncomeAmount = existing.SciIncomeAmount;
            }
        }
        else
        {
            // First visit: both heads default to Self, pre-filled with the member.
            vm.DiscomIncomeIdNo = vm.MemberIdNo;
            vm.DealCloseIdNo = vm.MemberIdNo;
        }

        return vm;
    }

    /// <summary>
    /// Turns one Self/Other choice into the IdNo + name that gets stored, adding a
    /// ModelState error instead when an "Other" ID fails the upline rule. The AJAX
    /// verify is a convenience; this is where the rule is actually enforced.
    /// </summary>
    private async Task<(string? IdNo, string? Name)> ResolveHeadAsync(
        RemainingBvViewModel vm, BvBeneficiaryMode mode, string? typedIdNo, string headLabel)
    {
        if (mode == BvBeneficiaryMode.Self)
            return (vm.MemberIdNo, vm.MemberName);

        var typed = (typedIdNo ?? string.Empty).Trim();
        if (typed.Length == 0)
        {
            ModelState.AddModelError(string.Empty, $"{headLabel}: you selected 'Other', so an ID number must be entered.");
            return (null, null);
        }

        var check = await _sponsorTree.CheckRelationAsync(vm.MemberIdNo, typed);
        if (!check.IsAcceptable)
        {
            ModelState.AddModelError(string.Empty, $"{headLabel}: {check.Message}");
            return (null, null);
        }

        return (check.IdNo, check.Name);
    }
}
