namespace SolarPortal.Application.Services;

/// <summary>
/// Reads the bank list from the legacy SolFit table M_BankMaster so the
/// withdrawal form offers the same banks as the old system:
///     SELECT BId, BankName FROM M_BankMaster
///     WHERE ActiveStatus='Y' AND RowStatus='Y' ORDER BY BankName
///
/// The master holds bank NAMES only - its IFSCode / AcNo / BranchName columns
/// are empty in the live data, so the worker types those himself.
/// </summary>
public interface IBankService
{
    Task<List<BankDto>> GetActiveAsync();
}

public class BankDto
{
    /// <summary>Legacy M_BankMaster.BId - stable id.</summary>
    public int BId { get; set; }
    /// <summary>Display name, e.g. "BANK OF BARODA".</summary>
    public string BankName { get; set; } = string.Empty;
}
