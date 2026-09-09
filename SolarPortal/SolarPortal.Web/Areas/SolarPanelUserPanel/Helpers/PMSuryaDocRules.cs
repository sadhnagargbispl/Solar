using SolarPortal.Application.DTOs;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Helpers;

/// <summary>
/// Shared PM Surya Ghar document rules, so the Upload page, the Dashboard and the
/// Status page all agree on "what the user still has to do".
/// </summary>
public static class PMSuryaDocRules
{
    /// <summary>
    /// The documents the user MUST upload (same list as the Upload page).
    /// Property Document is deliberately absent — it is optional now, so a member
    /// who cannot produce a Farad / पट्टा / Registry is no longer held at the PM
    /// Surya Ghar stage. It can still be uploaded, and is still reviewed when it is.
    /// </summary>
    public static readonly DocumentType[] RequiredTypes =
    {
        DocumentType.AadharCard,        // front
        DocumentType.AadharCardBack,    // back — Aadhaar is taken as two shots
        DocumentType.PANCard,
        DocumentType.LightBill,
        DocumentType.BankPassbook,
        DocumentType.GPSPhoto,
        DocumentType.Photo,
        DocumentType.Signature
    };

    /// <summary>Documents the user may upload but is never blocked on.</summary>
    public static readonly DocumentType[] OptionalTypes =
    {
        DocumentType.PropertyDocument
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
