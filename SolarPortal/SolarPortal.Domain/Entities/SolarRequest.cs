using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection.Metadata;

namespace SolarPortal.Domain.Entities;

public class SolarRequest : BaseEntity
{
    public string RequestNumber { get; set; } = string.Empty; // SCR-001
    public string UserId { get; set; } = string.Empty;

    // Applicant Info
    public string ApplicantName { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string? AlternateMobile { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PinCode { get; set; } = string.Empty;
    public string? AadharNumber { get; set; }
    public string? PANNumber { get; set; }

    // Technical
    public RequestType RequestType { get; set; } = RequestType.WithActivation;
    public ConnectionType ConnectionType { get; set; }
    public decimal KVCapacity { get; set; }
    public int? SolarProjectId { get; set; }
    public string? SelectedPlan { get; set; }
    public decimal PlanAmount { get; set; }

    // Legacy product reference. For "With Activation" mode, the user picks one of the
    // basic products from V#SpProductDetail (legacy cooperative DB view) — we store its
    // ProdId here. SolarProjectId is for our own SolarProjects master and is null in
    // this case. Only ONE of the two should be populated per request.
    public int? ExternalProductId { get; set; }

    // ─── Light Bill ownership (spec task 3) ──────────────────────────────
    // Asked at solar-request time: is the electricity bill in the applicant's own
    // name, or in a blood relation's name? If a blood relation, proof is mandatory.
    // Values: "Self" | "BloodRelation".
    public string? LightBillOwnership { get; set; }
    public string? LightBillRelationName { get; set; }   // relation holder's name (blood-relation case)
    public string? LightBillProofPath { get; set; }      // uploaded proof (blood-relation case)

    // ─── PM Surya Ghar options (spec tasks 7 & 11) ───────────────────────
    public string? PMSuryaLoanOption { get; set; }       // "Loan" | "WithoutLoan"
    public string? PMSuryaGharIdNo { get; set; }         // set by admin on approval
    public string? PmSuryaApplicationNo { get; set; }    // PM Surya Ghar ID written by the ADMIN panel app (shared DB column)

    // When the user pressed "Submit for Admin Verification" on the PM Surya page.
    // Documents uploaded at/before this moment are with the admin and can no
    // longer be replaced by the user; anything uploaded AFTER it is still the
    // user's own draft and stays replaceable until the next submit.
    public DateTime? PMSuryaSubmittedAt { get; set; }


    // ─── Mode history (which mode was taken, and when) ───────────────────
    // "Activate Now" upgrades a Without-Activation request by OVERWRITING
    // RequestType on this same row. Without the stamps below, nothing left in
    // the schema shows the member ever started without activation, so the
    // admin Activation History report cannot tell "started without activation,
    // activated later" from "always with activation".
    //
    // Each date is stamped ONCE, the first time that mode is entered, so an
    // upgrade adds WithActivationOn without disturbing WithoutActivationOn.
    // Columns added by ADD-SolarRequestModeHistory.sql.
    public RequestType? OriginalRequestType { get; set; }
    public DateTime? WithoutActivationOn { get; set; }
    public DateTime? WithActivationOn { get; set; }
    public DateTime? AlreadyActiveOn { get; set; }

    /// <summary>
    /// Image point 2: "Jo user solar le raha Without Activation / Only Solar — ya
    /// baad me ID active karta hai — wo user ID cPanel se PRODUCT ka request nahi
    /// kar paye, sirf ACTIVATION ka."
    ///
    /// Set the first time the request enters Only-Solar-Without-Activation mode and
    /// deliberately NEVER cleared afterwards: activating later must not re-open
    /// product ordering for that ID. The legacy cPanel product-request page reads
    /// this column to decide whether to offer product orders at all.
    ///
    /// Column added by ADD-UserPanelIncPoints.sql.
    /// </summary>
    public bool ProductRequestBlocked { get; set; }

    /// <summary>
    /// Moves the request into <paramref name="type"/> and records when that
    /// happened, keeping every earlier mode's date intact. Call this instead of
    /// assigning RequestType directly.
    /// </summary>
    public void StampMode(RequestType type, DateTime whenUtc)
    {
        RequestType = type;
        OriginalRequestType ??= type;
        switch (type)
        {
            case RequestType.OnlySolarWithoutActivation:
                WithoutActivationOn ??= whenUtc;
                // Image point 2 — from here on this ID may only ever request an
                // ACTIVATION, never a product. Sticky on purpose: the later
                // "Activate Now" upgrade calls StampMode(WithActivation) and must
                // not undo the block.
                ProductRequestBlocked = true;
                break;
            case RequestType.WithActivation:
                WithActivationOn ??= whenUtc;
                break;
            case RequestType.AlreadyActiveOnlyRequest:
                AlreadyActiveOn ??= whenUtc;
                break;
        }
    }

    /// <summary>True when this started as Without Activation and was upgraded later.</summary>
    public bool WasUpgradedToActivation =>
        OriginalRequestType == RequestType.OnlySolarWithoutActivation &&
        WithActivationOn.HasValue;

    // Status
    public ProjectStatus CurrentStage { get; set; } = ProjectStatus.Registration;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public string? RejectionReason { get; set; }
    public string? AdminNotes { get; set; }

    // Navigation
    public virtual ApplicationUser? User { get; set; }
    public virtual SolarProject? SolarProject { get; set; }
    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public virtual ICollection<Document> Documents { get; set; } = new List<Document>();
    public virtual ICollection<SiteSurvey> SiteSurveys { get; set; } = new List<SiteSurvey>();
    public virtual ICollection<MeterDispatch> MeterDispatches { get; set; } = new List<MeterDispatch>();
    public virtual ICollection<MaterialDispatch> MaterialDispatches { get; set; } = new List<MaterialDispatch>();
    public virtual ICollection<Installation> Installations { get; set; } = new List<Installation>();
    public virtual ICollection<DCRDocument> DCRDocuments { get; set; } = new List<DCRDocument>();
    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();
    public virtual Commission? Commission { get; set; }
    public virtual SolarAccount? SolarAccount { get; set; }

    // ─── Display-only field for admin views ──────────────────────────────
    // Populated by admin controllers via EnrichMemberNamesAsync (or left
    // null). Admin Razor views fall back to ApplicantName when this is
    // blank. Marked NotMapped so EF doesn't try to create a DB column.
    // Keeps the same shape as SolarRequestDto.MemberFullName so the views
    // can use either.
    [NotMapped]
    public string? MemberFullName { get; set; }
}