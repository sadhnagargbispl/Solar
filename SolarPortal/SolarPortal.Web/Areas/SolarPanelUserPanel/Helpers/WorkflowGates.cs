using SolarPortal.Application.DTOs;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Helpers;

/// <summary>
/// One place for the "which workflow menu is open yet" rules, so the sidebar,
/// the Site Survey page and the Meter Dispatch page can never disagree.
///
/// Image points 3 &amp; 4 (user panel):
///   • point 4 — "kisi bhi ID ka Solar Request approve ho jati hai to PM Surya
///                open ho jayega, pura payment ki zarurat nahi hai."
///   • point 3 — "Admin PM Surya ko approve hote hi Meter Dispatch &amp; Site
///                Survey ka menu open ho jaye."
/// </summary>
public static class WorkflowGates
{
    /// <summary>
    /// PM Surya Ghar is open once the request is APPROVED — payment does not have
    /// to be complete, and admin does not have to separately advance the stage.
    /// Requests already at/past the PMSurvey stage stay open as before.
    /// </summary>
    public static bool IsPMSuryaOpen(SolarRequest req) =>
        req.ApprovalStatus == ApprovalStatus.Approved ||
        req.CurrentStage >= ProjectStatus.PMSurvey;

    /// <summary>
    /// Site Survey opens the moment admin approves the PM Surya Ghar documents —
    /// it no longer waits for admin to finish Meter Dispatch first. (Meter Dispatch
    /// itself has no user page; it stays visible on the status timeline.)
    ///
    /// Two signals count as "approved", because the admin panel is a separate app
    /// on the shared DB and may do either (or both):
    ///   1. it advanced the request past PM Surya (stage ≥ MeterDispatch), or
    ///   2. it marked every required PM document Approved.
    /// </summary>
    public static bool IsPMSuryaApproved(SolarRequest req, IEnumerable<PMDocumentDto> pmDocs) =>
        req.CurrentStage >= ProjectStatus.MeterDispatch ||
        PMSuryaDocRules.AllRequiredApproved(pmDocs);
}
