namespace SolarPortal.Domain.Entities;

// INC worker withdrawal request. Maps to the EXISTING IncWithdrawals table
// (shared) which has no BaseEntity columns (no IsDeleted / CreatedAt).
public class IncWithdrawal
{
    public int Id { get; set; }
    public int WorkerId { get; set; }
    public string? RequestNumber { get; set; }
    public decimal Amount { get; set; }

    // Free-text summary. Predates the split into proper bank fields below and
    // is still written with a readable one-liner so old rows and new rows read
    // the same way in any report that only knows about this column.
    public string? BankDetails { get; set; }

    // Bank is picked from the legacy M_BankMaster list; the worker types the rest.
    public string? BankName { get; set; }
    public string? IFSCode { get; set; }
    public string? BranchName { get; set; }
    public string? AccountNo { get; set; }

    public string Status { get; set; } = "Pending";   // Pending / Approved / Rejected
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? ProcessedBy { get; set; }
    public string? AdminNotes { get; set; }
    public string? RejectionReason { get; set; }
}
