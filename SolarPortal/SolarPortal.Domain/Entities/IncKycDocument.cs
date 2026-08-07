using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// KYC record of a COMMISSION-earning (INC) installer — one row per worker.
///
/// Image point 8 (INC panel → admin): "INC — commission wale ka KYC upload system
/// banana hai, approve by admin. JOB wale ka KYC nahi lena hai." So a row here only
/// ever belongs to a Worker whose Type is <see cref="WorkerType.INC"/>; the
/// controller re-checks that from the Workers table on every write.
///
/// Shape deliberately mirrors the existing member-panel KYC page (legacy KYC.aspx):
/// three independently-verified sections — Address Proof, Bank Detail, PAN Card —
/// each with its own status, reject remark and reject reason. Admin (separate app,
/// shared DB) sets those; a section that comes back Rejected unlocks for the
/// installer to correct, exactly like the legacy page does.
/// </summary>
public class IncKycDocument : BaseEntity
{
    public int WorkerId { get; set; }

    // ─── Section 1: Address Proof ────────────────────────────────────────────
    public string? Address { get; set; }
    public string? PinCode { get; set; }
    /// <summary>Legacy M_StateDivMaster.StateCode.</summary>
    public string? StateCode { get; set; }
    public string? StateName { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }

    /// <summary>Legacy M_IdTypeMaster.Id — which paper is being used as address proof.</summary>
    public int? IdProofTypeId { get; set; }
    public string? IdProofTypeName { get; set; }
    /// <summary>Number printed on that paper (Aadhaar no., voter id no., …).</summary>
    public string? IdProofNo { get; set; }

    public string? AddressProofFrontPath { get; set; }
    public string? AddressProofBackPath { get; set; }

    public ApprovalStatus AddressStatus { get; set; } = ApprovalStatus.Pending;
    /// <summary>Admin's reject remark for the address section.</summary>
    public string? AddressRemark { get; set; }
    public DateTime? AddressReviewedAt { get; set; }
    public string? AddressReviewedBy { get; set; }

    // ─── Section 2: Bank Detail ──────────────────────────────────────────────
    /// <summary>"SAVING ACCOUNT" / "CURRENT ACCOUNT" — same values as the legacy page.</summary>
    public string? AccountType { get; set; }
    public string? AccountNo { get; set; }
    /// <summary>Legacy M_BankMaster.BId.</summary>
    public int? BankId { get; set; }
    public string? BankName { get; set; }
    public string? BranchName { get; set; }
    public string? IfscCode { get; set; }
    public string? BankProofPath { get; set; }

    public ApprovalStatus BankStatus { get; set; } = ApprovalStatus.Pending;
    public string? BankRemark { get; set; }
    public DateTime? BankReviewedAt { get; set; }
    public string? BankReviewedBy { get; set; }

    // ─── Section 3: PAN Card ─────────────────────────────────────────────────
    public string? PanNo { get; set; }
    public string? PanProofPath { get; set; }

    public ApprovalStatus PanStatus { get; set; } = ApprovalStatus.Pending;
    public string? PanRemark { get; set; }
    public DateTime? PanReviewedAt { get; set; }
    public string? PanReviewedBy { get; set; }

    /// <summary>Last time the installer submitted or corrected anything.</summary>
    public DateTime? SubmittedAt { get; set; }

    public virtual Worker? Worker { get; set; }

    // ─── Derived helpers (not mapped) ────────────────────────────────────────

    /// <summary>
    /// A section is editable while it has never been filled, or after admin
    /// rejected it. Once submitted (Pending) or approved it locks — same rule the
    /// legacy KYC page applies field by field.
    /// </summary>
    public static bool SectionEditable(ApprovalStatus status, bool hasFile) =>
        status == ApprovalStatus.Rejected || !hasFile;

    public bool AddressEditable => SectionEditable(AddressStatus, !string.IsNullOrWhiteSpace(AddressProofFrontPath));
    public bool BankEditable    => SectionEditable(BankStatus,    !string.IsNullOrWhiteSpace(BankProofPath));
    public bool PanEditable     => SectionEditable(PanStatus,     !string.IsNullOrWhiteSpace(PanProofPath));

    /// <summary>True once admin has approved all three sections.</summary>
    public bool IsFullyApproved =>
        AddressStatus == ApprovalStatus.Approved &&
        BankStatus == ApprovalStatus.Approved &&
        PanStatus == ApprovalStatus.Approved;
}
