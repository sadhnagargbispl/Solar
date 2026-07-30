namespace SolarPortal.Application.Services;

/// <summary>One line of the INC wallet statement.</summary>
public class IncWalletEntry
{
    public string VoucherNo { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Narration { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool IsCredit { get; set; } = true;
    public string RefNo { get; set; } = string.Empty;
}

/// <summary>Outcome of trying to credit an installation commission.</summary>
public class IncCommissionResult
{
    public bool Credited { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal TdsAmount { get; set; }
    public decimal NetAmount { get; set; }
    public bool WalletPosted { get; set; }   // true when a TrnVoucher row was written
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// INC worker commission + wallet.
///
/// Jab installer "Mark Complete" karta hai, us project ke SolarProject ke liye
/// CommissionMasters mein set percent uthta hai, PlanAmount par commission banta
/// hai, 1% TDS katta hai aur net amount INC wallet mein credit ho jata hai.
///
/// Do jagah likha jata hai:
///   1. IncCommissionLedger  - hamari apni ledger row (hamesha).
///   2. TrnVoucher AcType='I' - legacy SolFit "INC Wallet" (sirf tab jab worker ka
///      member IdNo Workers.UserId mein bhara ho).
///
/// Withdraw page isi balance par chalta hai.
/// </summary>
public interface IIncWalletService
{
    /// <summary>
    /// Credits the installation commission for one request. Idempotent — a second
    /// call for the same (worker, request) returns without crediting again.
    /// </summary>
    Task<IncCommissionResult> CreditInstallationCommissionAsync(int solarRequestId, int workerId, string performedBy);

    /// <summary>
    /// Posts the withdrawal debit into the INC wallet - exactly the amount the
    /// user asked for, on the logged-in INC user's own id. Returns false with a
    /// reason when nothing was posted (e.g. this request was already debited).
    /// </summary>
    Task<(bool posted, string message)> DebitForWithdrawalAsync(int workerId, decimal amount, string withdrawalRef);

    /// <summary>Net (post-TDS) commission credited to this worker in our ledger.</summary>
    Task<decimal> GetLedgerNetAsync(int workerId);

    /// <summary>
    /// Wallet statement for the Wallet page - narration, amount, date, voucher no.
    /// Reads dbo.IncTrnvoucher for the logged-in INC user - credits from completed
    /// installations and debits from withdrawal requests, newest first. Kept as one
    /// list so the page can show a running passbook.
    /// </summary>
    Task<List<IncWalletEntry>> GetWalletEntriesAsync(int workerId);

    /// <summary>INC wallet balance (IncTrnvoucher credits minus debits).</summary>
    Task<decimal> GetWalletBalanceAsync(int workerId);
}
