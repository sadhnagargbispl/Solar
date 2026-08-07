namespace SolarPortal.Web.Areas.SolarPanelUserPanel.Helpers;

/// <summary>
/// Turns whatever Identity hands us back into the raw legacy IdNo that
/// M_MemberMaster.Idno actually stores.
///
/// LiveDbAuthBridge stores the raw IdNo (e.g. "SADHNATEST05") as
/// ApplicationUser.Id, but older / alternate sign-in paths sometimes surface the
/// synthetic UserName "member-SADHNATEST05@livedb.local" instead. Passing that
/// straight to a legacy lookup silently finds nothing, so every legacy call
/// normalises first.
/// </summary>
public static class MemberIdNo
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        if (s.StartsWith("member-", StringComparison.OrdinalIgnoreCase))
            s = s.Substring("member-".Length);
        var atIdx = s.IndexOf('@');
        if (atIdx > 0) s = s.Substring(0, atIdx);
        return s.Trim();
    }
}
