namespace SolarPortal.Domain.Enums;

public enum ProjectStatus
{
    Registration = 1,
    ProductSelection = 2,
    Payment = 3,
    PMSurvey = 4,
    MeterDispatch = 5,
    SiteSurvey = 6,
    MaterialDispatch = 7,
    Installation = 8,
    DCRUpdate = 9,
    Completed = 10
}

public enum ApprovalStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3
}

public enum ConnectionType
{
    Domestic = 1,
    Commercial = 2
}

public enum PaymentStatus
{
    Pending = 1,
    Partial = 2,
    Completed = 3,
    Rejected = 4   // Admin rejected this submission; user must submit a fresh one.
}

public enum DocumentType
{
    AadharCard = 1,
    PANCard = 2,
    BankPassbook = 3,
    LightBill = 4,
    PropertyDocument = 5,
    PaymentReceipt = 6,
    GPSPhoto = 7,
    DCRDocument = 8,
    SitePhoto = 9,
    PMSuryagramDocument = 10,
    // PM Surya Ghar - applicant photo & signature (image point 6)
    // NOTE: values 11-14 are used by the Admin project on the shared
    // PMDocuments.DocumentType column, so we use 15/16 to avoid a clash.
    Photo = 15,
    Signature = 16
}

public enum RequestType
{
    WithActivation = 1,
    OnlySolarWithoutActivation = 2,
    AlreadyActiveOnlyRequest = 3
}

public enum WorkerType
{
    JOB = 1,
    INC = 2
}

/// <summary>
/// Who receives one of the Remaining-BV income heads (Discom Income / Deal Close).
/// Self  = the member's own IdNo is saved.
/// Other = a typed-in IdNo, which must sit ABOVE the member in the sponsor tree.
/// </summary>
public enum BvBeneficiaryMode
{
    Self = 1,
    Other = 2
}

/// <summary>
/// Where a typed-in IdNo sits relative to the member in the SPONSOR tree
/// (m_membermaster.RefFormNo chain). Only <see cref="Upline"/> may be saved
/// against a Remaining-BV income head.
/// </summary>
public enum SponsorRelation
{
    /// <summary>No member with that IdNo exists.</summary>
    NotFound = 0,
    /// <summary>The typed IdNo IS the member - they should pick "Self" instead.</summary>
    Self = 1,
    /// <summary>Sponsor / sponsor-of-sponsor / ... - the only accepted relation.</summary>
    Upline = 2,
    /// <summary>Somewhere below the member in the sponsor tree - rejected.</summary>
    Downline = 3,
    /// <summary>A real member, but on a different branch entirely - rejected.</summary>
    Unrelated = 4
}