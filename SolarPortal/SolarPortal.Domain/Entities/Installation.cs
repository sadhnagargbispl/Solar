using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class Installation : BaseEntity
{
    public int SolarRequestId { get; set; }
    public DateTime? InstallationDate { get; set; }
    public string? Notes { get; set; }
    public string? Remark { get; set; }
    public int? AssignedWorkerId { get; set; }
    public bool IsCompleted { get; set; } = false;
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// First photo of the set. Kept as-is so existing screens that read one photo
    /// keep working; the complete set lives in <see cref="Photos"/>.
    /// </summary>
    public string? CompletionPhotoPath { get; set; }

    // ─── Admin verification of the marked installation (image point 11) ──────
    // "Ye admin ko show hona chahiye. Admin ko approve hone par credit hona
    // chahiye. Reject hone par INC wala wapas update karega."
    //
    // Columns added by ADD-UserPanelIncPoints.sql.

    /// <summary>
    /// Pending until admin reviews the photos, then Approved (commission is
    /// credited) or Rejected (the installer re-uploads and it goes back to
    /// Pending). Existing rows default to Pending.
    /// </summary>
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;

    /// <summary>Why admin sent it back — shown to the installer verbatim.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>When admin approved / rejected.</summary>
    public DateTime? ReviewedAt { get; set; }

    /// <summary>Admin user who approved / rejected.</summary>
    public string? ReviewedBy { get; set; }

    /// <summary>Last time the installer submitted (or re-submitted) for review.</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>
    /// Set once the INC commission for this installation has actually been posted,
    /// so a repeated approval sweep can never double-pay.
    /// </summary>
    public bool CommissionCredited { get; set; }

    public virtual Worker? AssignedWorker { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
    public virtual ICollection<WorkerAssignment> WorkerAssignments { get; set; } = new List<WorkerAssignment>();
    public virtual ICollection<InstallationPhoto> Photos { get; set; } = new List<InstallationPhoto>();
}