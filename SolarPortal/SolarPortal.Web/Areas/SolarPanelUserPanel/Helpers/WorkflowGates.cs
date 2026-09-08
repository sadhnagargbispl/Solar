using SolarPortal.Application.DTOs;
using SolarPortal.Application.Services;
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

    /// <summary>
    /// "Installation complete hone ke baad remaining BV le sakta hai, baaki nahi
    /// le sakta." — Remaining BV opens ONLY once the installation is finished.
    /// An approved solar request on its own is no longer enough.
    ///
    /// Two signals count as finished, because the admin panel is a separate app on
    /// the shared DB and may do either (or both):
    ///   1. the request moved PAST the Installation stage (DCR Update / Completed), or
    ///   2. an installation row is marked complete AND admin-approved.
    ///
    /// A merely submitted (Pending) or rejected installation does NOT count: the
    /// photos are still under review, and the INC commission does not move on it
    /// either, so the left-over BV must not be distributable yet.
    /// </summary>
    public static bool IsInstallationComplete(SolarRequest req, IEnumerable<Installation>? installations = null) =>
        req.CurrentStage > ProjectStatus.Installation ||
        (installations ?? req.Installations)
            .Any(i => i.IsCompleted && i.ApprovalStatus == ApprovalStatus.Approved);

    /// <summary>
    /// What is still owed on a plan after everything that counts as paid —
    /// admin-VERIFIED payments plus any already-active cPanel deposit. Never
    /// negative, so an overpaid project simply reads 0.
    ///
    /// Submitted-but-unverified money deliberately does NOT count: the admin has
    /// not confirmed it reached the company yet.
    /// </summary>
    public static decimal OutstandingAmount(decimal planAmount, decimal paidTowardsPlan) =>
        Math.Max(0m, planAmount - paidTowardsPlan);

    /// <summary>
    /// "Full paid amount hone ke baad hi remaining BV update kar sakta hai." —
    /// the whole plan amount has to be cleared (verified payments + deposit)
    /// before the left-over BV may be distributed. Used together with
    /// <see cref="IsInstallationComplete"/>: BOTH have to pass.
    /// </summary>
    public static bool IsFullyPaid(decimal planAmount, decimal paidTowardsPlan) =>
        OutstandingAmount(planAmount, paidTowardsPlan) <= 0m;

    /// <summary>
    /// Verified money that unlocks "Activate Now": the usual ₹20,000 floor, but
    /// never more than the plan itself, so a plan cheaper than the floor is
    /// unlocked by paying it off.
    /// </summary>
    public static decimal ActivationMinimum(decimal requestedAmount) =>
        Math.Min(PaymentService.MinimumPaymentThreshold, requestedAmount);

    /// <summary>
    /// "Dashboard par ID active ka option — minimum 20k approve ho jate hain to
    /// wo option available hona chahiye."
    ///
    /// Only a request registered as "Only Solar — Without Activation" can be
    /// activated at all, and the option appears as soon as the admin has VERIFIED
    /// <see cref="ActivationMinimum"/>. It no longer waits for the plan to be paid
    /// in full — the balance stays payable on the Payment page afterwards.
    /// </summary>
    public static bool IsActivationEligible(RequestType requestType, decimal requestedAmount, decimal verifiedPaid) =>
        requestType == RequestType.OnlySolarWithoutActivation
        && requestedAmount > 0
        && verifiedPaid >= ActivationMinimum(requestedAmount);
}
