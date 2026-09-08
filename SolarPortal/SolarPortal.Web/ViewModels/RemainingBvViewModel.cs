using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.ViewModels;

/// <summary>
/// Backs the member's "Update Remaining BV" form and report.
///
/// Everything except the two beneficiary choices is read-only on screen. Save
/// re-resolves the sponsor, the BV figures and the per-head amounts server-side
/// and ignores whatever the form posted for them, so a hand-edited form can only
/// ever change who receives Discom Income and Deal Close.
/// </summary>
public class RemainingBvViewModel
{
    public int RequestId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;

    // ─── Member + sponsor (auto) ──────────────────────────────────────────
    public string MemberIdNo { get; set; } = string.Empty;
    public string? MemberName { get; set; }
    public decimal MemberFormNo { get; set; }
    public string? SponsorIdNo { get; set; }
    public string? SponsorName { get; set; }

    // ─── Verified product order ───────────────────────────────────────────
    public string? PlanName { get; set; }
    public decimal SolarTypeKV { get; set; }
    public string? OrderNo { get; set; }
    public string? ProductName { get; set; }

    // ─── BV snapshot ──────────────────────────────────────────────────────
    public decimal TotalBV { get; set; }
    public decimal FixedBV { get; set; }
    public decimal RemainingBV { get; set; }
    public string BvSource { get; set; } = string.Empty;

    // ─── Income heads ─────────────────────────────────────────────────────
    public BvBeneficiaryMode DiscomIncomeMode { get; set; } = BvBeneficiaryMode.Self;
    public string? DiscomIncomeIdNo { get; set; }
    public string? DiscomIncomeName { get; set; }
    public decimal DiscomIncomeAmount { get; set; }

    public BvBeneficiaryMode DealCloseMode { get; set; } = BvBeneficiaryMode.Self;
    public string? DealCloseIdNo { get; set; }
    public string? DealCloseName { get; set; }
    public decimal DealCloseAmount { get; set; }

    /// <summary>Always the sponsor - shown read-only.</summary>
    public string? SciIncomeIdNo { get; set; }
    public string? SciIncomeName { get; set; }
    public decimal SciIncomeAmount { get; set; }

    public decimal TotalIncome => DiscomIncomeAmount + DealCloseAmount + SciIncomeAmount;

    // ─── State ────────────────────────────────────────────────────────────
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? RejectedAt { get; set; }
    public DateTime? PayoutPostedAt { get; set; }
    public string? AdminRemark { get; set; }
    public string? RejectionReason { get; set; }

    /// <summary>True once a row exists - i.e. the member has already submitted.</summary>
    public bool IsSubmitted { get; set; }

    /// <summary>Admin gave final approval: nobody may change anything, ever again.</summary>
    public bool IsLocked => Status == ApprovalStatus.Approved;

    /// <summary>Admin sent it back. The member fixes it and saves onto the same row.</summary>
    public bool IsRejected => Status == ApprovalStatus.Rejected;

    /// <summary>
    /// The member may edit only before the first submit, or after the admin has
    /// sent the record back. Once submitted it sits with the admin and is
    /// read-only, so a record cannot be changed under the admin mid-verification.
    /// </summary>
    public bool CanEdit => !IsSubmitted || IsRejected;
}

/// <summary>One row of the member's read-only Remaining BV report.</summary>
public class RemainingBvReportRow
{
    public int Id { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public string? PlanName { get; set; }
    public decimal SolarTypeKV { get; set; }
    public string? ProductName { get; set; }
    public string? OrderNo { get; set; }

    public decimal TotalBV { get; set; }
    public decimal FixedBV { get; set; }
    public decimal RemainingBV { get; set; }
    public string? BvSource { get; set; }

    public string? SponsorIdNo { get; set; }
    public string? SponsorName { get; set; }

    public BvBeneficiaryMode DiscomIncomeMode { get; set; }
    public string? DiscomIncomeIdNo { get; set; }
    public string? DiscomIncomeName { get; set; }
    public decimal DiscomIncomeAmount { get; set; }

    public BvBeneficiaryMode DealCloseMode { get; set; }
    public string? DealCloseIdNo { get; set; }
    public string? DealCloseName { get; set; }
    public decimal DealCloseAmount { get; set; }

    public string? SciIncomeIdNo { get; set; }
    public string? SciIncomeName { get; set; }
    public decimal SciIncomeAmount { get; set; }

    public decimal TotalIncome => DiscomIncomeAmount + DealCloseAmount + SciIncomeAmount;

    public ApprovalStatus Status { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PayoutPostedAt { get; set; }
    public string? AdminRemark { get; set; }
    public string? RejectionReason { get; set; }
}
