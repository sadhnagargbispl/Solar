using SolarPortal.Application.DTOs;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Helpers;

/// <summary>
/// Shared PM Surya Ghar document rules, so the Upload page, the Dashboard and the
/// Status page all agree on "what the user still has to do".
/// </summary>
public static class PMSuryaDocRules
{
    /// <summary>The 8 documents the user must upload (same list as the Upload page).</summary>
    public static readonly DocumentType[] RequiredTypes =
    {
        DocumentType.AadharCard,
        DocumentType.PANCard,
        DocumentType.LightBill,
        DocumentType.BankPassbook,
        DocumentType.PropertyDocument,
        DocumentType.GPSPhoto,
        DocumentType.Photo,
        DocumentType.Signature
    };

    /// <summary>
    /// The ADMIN panel (separate app, shared DB) saves its approval uploads with
    /// DocumentType 11-14 and/or under a "pmsurya-approval" folder, without setting
    /// IsAdminUpload - detect all three markers so they aren't counted as user uploads.
    /// </summary>
    public static bool IsAdminDoc(PMDocumentDto d) =>
        d.IsAdminUpload
        || ((int)d.DocumentType >= 11 && (int)d.DocumentType <= 14)
        || (d.FilePath != null && d.FilePath.IndexOf("pmsurya-approval", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>
    /// True once every required document is uploaded AND approved by admin - i.e. the
    /// user has nothing left to do on the PM Surya Ghar stage.
    /// </summary>
    public static bool AllRequiredApproved(IEnumerable<PMDocumentDto> docs)
    {
        var userDocs = docs.Where(d => !IsAdminDoc(d)).ToList();
        return RequiredTypes.All(t =>
            userDocs.Any(d => d.DocumentType == t && d.Status == ApprovalStatus.Approved));
    }
}
