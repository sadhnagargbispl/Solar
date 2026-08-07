namespace SolarPortal.Application.Services;

/// <summary>
/// Bridges new SolarPortal "With Activation" submissions into the
/// legacy SolFit ASP.NET (VB) table TrnProductorderDetail.
///
/// Per spec ("Request.QueryString tp=A wala hi work karna h ism"):
///   Jab user new SolarPortal mein "With Activation" mode select karke
///   product pick aur submit kare, woh row legacy table mein bhi insert
///   hona chahiye taaki existing legacy reports / activation workflow
///   unko process kar sake. Yeh dual-write hai — new app ka apna
///   SolarRequest record alag rehta hai (workflow, status, payments).
///
/// All fields mirror the original VB Insert in ProductWalletRequest.aspx.vb
/// (RequestPin / "tp=A" branch). Where the legacy code reads from the
/// admin form (transaction no, dispatch date, address, image), we pass
/// values that the user already entered in the new payment + profile
/// flow. Image (ImageUpload column) is copied to wwwroot/Images/UploadImage
/// so the legacy admin viewer can still display it.
///
/// ApproveOrRejectProductRequestAsync mirrors the SECOND half of the legacy
/// flow — the admin approve/reject page's chkSelect-checked block (posting
/// Repurchincome, calling Sp_ActivateMember_New, and creating the TrnOrder
/// header for non-Cash pay modes). InsertWithActivationAsync can optionally
/// call this automatically right after a successful insert when
/// LegacyProductRequestInput.AutoProcessApproval is true.
/// </summary>
public interface ILegacyProductRequestService
{
    /// <summary>
    /// Creates a TrnProductorderDetail row for the picked product.
    /// Returns the assigned OrderNo so callers can store / log it.
    /// If input.AutoProcessApproval is true, also runs the approve/reject
    /// step on that OrderNo before returning (result.ApprovalResult).
    /// </summary>
    Task<LegacyInsertResult> InsertWithActivationAsync(LegacyProductRequestInput input);

    /// <summary>
    /// Port of the legacy admin approve/reject page's chkSelect-checked
    /// block: duplicate-processing guard, optional kit (TopUp) lookup,
    /// session-id resolution, member lookup, Cash vs non-Cash order
    /// creation, Repurchincome posting + member activation, and the final
    /// Approve/Reject status update on TrnProductorderDetail.
    /// </summary>
    Task<LegacyApprovalResult> ApproveOrRejectProductRequestAsync(LegacyApprovalInput input);

    /// <summary>
    /// Total money this member has ALREADY put in through the legacy cPanel
    /// product/activation orders — i.e. SUM(NetAmount) over their approved
    /// TrnProductorderDetail rows.
    ///
    /// Image point 1 (user panel): "Agar ID already active hai to jis order ka
    /// amount jama kiya hai wo Solar Panel par jama dikhe, aur Solar ki request
    /// lagate time utna amount kam lage."
    ///
    /// Returns 0 when the member can't be resolved or has no approved order, so
    /// callers can treat it as "no deposit" without special-casing failures.
    /// </summary>
    Task<decimal> GetApprovedOrderAmountAsync(string memberIdNo);
}

public class LegacyProductRequestInput
{
    /// <summary>Legacy m_membermaster.Formno — bridge resolves from IdNo.</summary>
    public string MemberIdNo { get; set; } = string.Empty;
    /// <summary>Selected product ID from V#SpProductDetail (BasicProductDto.ProdId).</summary>
    public int ProductId { get; set; }
    /// <summary>Quantity ordered. Defaults to 1 in the With Activation flow.</summary>
    public int Qty { get; set; } = 1;
    /// <summary>Payment UTR / Transaction number from new payment form.</summary>
    public string? TxnId { get; set; }
    /// <summary>Payment date.</summary>
    public DateTime? TxnDate { get; set; }
    /// <summary>Payment mode (PID into M_PayModeMaster). Defaults to UPI=1 if unknown.</summary>
    public int PayModeId { get; set; } = 1;
    /// <summary>Optional uploaded image filename — caller saves the bytes first.</summary>
    public string? ImageFileName { get; set; }
    /// <summary>Delivery / billing address from profile.</summary>
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string? PinCode { get; set; }
    public string? StateName { get; set; }
    public string? StateCode { get; set; }

    // ─── Fields for optional auto-approval right after insert ─────────────

    /// <summary>
    /// If true, InsertWithActivationAsync automatically calls
    /// ApproveOrRejectProductRequestAsync right after a successful insert.
    /// If false (default), the insert just creates the pending order and
    /// approval happens later (e.g. from an admin approve/reject action).
    /// </summary>
    public bool AutoProcessApproval { get; set; } = false;

    /// <summary>Reqno shown in the audit Remark text (lblReqno.Text in VB). Optional.</summary>
    public string? ReqNo { get; set; }

    /// <summary>Order amount — used for the kit (TopUp) lookup when ActType = "A".</summary>
    public decimal Amount { get; set; }

    /// <summary>Pay mode NAME, e.g. "Cash" (lblPaymode.Text in VB). Not the same as PayModeId.</summary>
    public string? PayModeName { get; set; }

    /// <summary>Activation type — "A" triggers the kit lookup (LblActype.Text in VB).</summary>
    public string? ActType { get; set; } = "A";   // bridge always inserts ForType='A' rows

    /// <summary>"Y" to approve, anything else to reject. Defaults to "Y" if not set.</summary>
    public string? ApproveType { get; set; } = "Y";

    /// <summary>Admin's free-text remark for the approval (TxtARemark.Text in VB).</summary>
    public string? ApproveRemark { get; set; }

    /// <summary>Approving party's code, used as SoldBy on the Repurchincome row (Session("WR") in VB).</summary>
    public string? PartyCode { get; set; }

    /// <summary>Linked inventory database name (Session("InvDatabase"+CompID) in VB).</summary>
    public string? InvDatabaseName { get; set; } = "solfitenergyinv";
}

public class LegacyInsertResult
{
    public bool Success { get; set; }
    public string? OrderNo { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Populated only when AutoProcessApproval was true on the input.
    /// Null means auto-approval wasn't requested; check .Success on this to
    /// see whether the approval step itself succeeded (the insert can
    /// succeed even if this later fails — the two run as separate
    /// transactions).
    /// </summary>
    public LegacyApprovalResult? ApprovalResult { get; set; }
}

/// <summary>Input for <see cref="ILegacyProductRequestService.ApproveOrRejectProductRequestAsync"/>.</summary>
public class LegacyApprovalInput
{
    /// <summary>TrnProductorderDetail.OrderNo (LblOrderNo.Text in VB)</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>Member's Formno (lbl.Text / LblGrpID in VB)</summary>
    public string FormNo { get; set; } = string.Empty;

    /// <summary>Member's IdNo (lblIdno.Text in VB)</summary>
    public string MemberIdNo { get; set; } = string.Empty;

    /// <summary>lblReqno.Text in VB — used only in the audit Remark text</summary>
    public string ReqNo { get; set; } = string.Empty;

    /// <summary>LblAmount.Text in VB — used for the kit lookup when ActType = "A"</summary>
    public decimal Amount { get; set; }

    /// <summary>lblPaymode.Text in VB — "Cash" takes the Repurchincome-only path</summary>
    public string PayMode { get; set; } = string.Empty;

    /// <summary>LblActype.Text in VB — "A" triggers the kit (TopUp) lookup</summary>
    public string ActType { get; set; } = string.Empty;

    /// <summary>"Y" = approve, anything else = reject (AprvType in VB)</summary>
    public string ApproveType { get; set; } = string.Empty;

    /// <summary>TxtARemark.Text in VB — admin's free-text remark</summary>
    public string ApproveRemark { get; set; } = string.Empty;

    /// <summary>Session("WR") in VB — the approving party's code, used as SoldBy</summary>
    public string PartyCode { get; set; } = string.Empty;

    /// <summary>
    /// Session("InvDatabase" & Session("CompID")) in VB — the linked
    /// inventory database name (holds M_ProductMaster, M_FiscalMaster,
    /// TrnPaymentConfirmation). Validated before use (identifier-only
    /// characters) since it's interpolated into SQL as a schema name.
    /// </summary>
    public string InvDatabaseName { get; set; } = "solfitenergyinv";
}

/// <summary>Result of <see cref="ILegacyProductRequestService.ApproveOrRejectProductRequestAsync"/>.</summary>
public class LegacyApprovalResult
{
    public bool Success { get; set; }
    public bool AlreadyProcessed { get; set; }
    public string? ErrorMessage { get; set; }
}
//namespace SolarPortal.Application.Services;

///// <summary>
///// Bridges new SolarPortal "With Activation" submissions into the
///// legacy SolFit ASP.NET (VB) table TrnProductorderDetail.
/////
///// Per spec ("Request.QueryString tp=A wala hi work karna h ism"):
/////   Jab user new SolarPortal mein "With Activation" mode select karke
/////   product pick aur submit kare, woh row legacy table mein bhi insert
/////   hona chahiye taaki existing legacy reports / activation workflow
/////   unko process kar sake. Yeh dual-write hai — new app ka apna
/////   SolarRequest record alag rehta hai (workflow, status, payments).
/////
///// All fields mirror the original VB Insert in ProductWalletRequest.aspx.vb
///// (RequestPin / "tp=A" branch). Where the legacy code reads from the
///// admin form (transaction no, dispatch date, address, image), we pass
///// values that the user already entered in the new payment + profile
///// flow. Image (ImageUpload column) is copied to wwwroot/Images/UploadImage
///// so the legacy admin viewer can still display it.
///// </summary>
//public interface ILegacyProductRequestService
//{
//    /// <summary>
//    /// Creates a TrnProductorderDetail row for the picked product.
//    /// Returns the assigned OrderNo so callers can store / log it.
//    /// </summary>
//    Task<LegacyInsertResult> InsertWithActivationAsync(LegacyProductRequestInput input);
//}

//public class LegacyProductRequestInput
//{
//    /// <summary>Legacy m_membermaster.Formno — bridge resolves from IdNo.</summary>
//    public string MemberIdNo { get; set; } = string.Empty;
//    /// <summary>Selected product ID from V#SpProductDetail (BasicProductDto.ProdId).</summary>
//    public int ProductId { get; set; }
//    /// <summary>Quantity ordered. Defaults to 1 in the With Activation flow.</summary>
//    public int Qty { get; set; } = 1;
//    /// <summary>Payment UTR / Transaction number from new payment form.</summary>
//    public string? TxnId { get; set; }
//    /// <summary>Payment date.</summary>
//    public DateTime? TxnDate { get; set; }
//    /// <summary>Payment mode (PID into M_PayModeMaster). Defaults to UPI=1 if unknown.</summary>
//    public int PayModeId { get; set; } = 1;
//    /// <summary>Optional uploaded image filename — caller saves the bytes first.</summary>
//    public string? ImageFileName { get; set; }
//    /// <summary>Delivery / billing address from profile.</summary>
//    public string? Address { get; set; }
//    public string? City { get; set; }
//    public string? District { get; set; }
//    public string? PinCode { get; set; }
//    public string? StateName { get; set; }
//    public string? StateCode { get; set; }

//}

//public class LegacyInsertResult
//{
//    public bool Success { get; set; }
//    public string? OrderNo { get; set; }
//    public string? ErrorMessage { get; set; }
//}
