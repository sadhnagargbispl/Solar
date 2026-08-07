namespace SolarPortal.Application.Services;

/// <summary>
/// Reads the address-proof types from the legacy SolFit table M_IdTypeMaster so
/// the INC KYC page offers exactly the same list as the old member-panel KYC
/// page (KYC.aspx → FillIdtypeMaster):
///     SELECT Id, IdType FROM M_IdTypeMaster WHERE ActiveStatus = 'Y'
/// </summary>
public interface IIdProofTypeService
{
    Task<List<IdProofTypeDto>> GetActiveAsync();
}

public class IdProofTypeDto
{
    /// <summary>Legacy M_IdTypeMaster.Id — stable id.</summary>
    public int Id { get; set; }
    /// <summary>Display name, e.g. "AADHAAR CARD", "VOTER ID".</summary>
    public string IdType { get; set; } = string.Empty;
}
