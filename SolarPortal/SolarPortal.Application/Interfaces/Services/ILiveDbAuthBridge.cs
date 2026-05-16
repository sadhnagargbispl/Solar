namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Bridge between the live DB (m_membermaster/m_usermaster) and Identity.
/// Strategy: when a login attempt comes in, first check the live DB. If the
/// credentials match there, ensure a matching Identity user exists with the
/// right role, then let Identity sign the user in normally.
/// </summary>
public interface ILiveDbAuthBridge
{
    /// <summary>
    /// Try to authenticate against m_membermaster. If credentials match,
    /// ensures an Identity user (email = synthetic, role = User) exists with
    /// the same password and returns the synthetic email (used to sign in).
    /// Returns null on failure.
    /// </summary>
    Task<string?> TryBridgeUserAsync(string idNo, string password);

    /// <summary>
    /// Same for m_usermaster — role = Admin. Returns synthetic email or null.
    /// </summary>
    Task<string?> TryBridgeAdminAsync(string userName, string password);
}
