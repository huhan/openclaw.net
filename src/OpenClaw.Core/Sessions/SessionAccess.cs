using OpenClaw.Core.Models;

namespace OpenClaw.Core.Sessions;

/// <summary>
/// Write access to a session. Owned sessions accept turns from the owner or an admin. Unowned sessions (created
/// by channels, cron, or before ownership existed) stay open, and writing to one never claims it. Callers without
/// an account are admin-equivalent credentials (bootstrap, open loopback) or channel and system flows, which
/// address sessions by their own keys.
/// </summary>
public static class SessionAccess
{
    public const string DeniedMessage = "This conversation belongs to another account.";

    public static bool CanWrite(Session session, string? accountId, bool isAdmin)
        => string.IsNullOrWhiteSpace(session.OwnerAccountId)
           || string.IsNullOrWhiteSpace(accountId)
           || isAdmin
           || string.Equals(session.OwnerAccountId, accountId, StringComparison.Ordinal);

    /// <summary>
    /// Read visibility for a resolved session. Owned sessions are visible to their owner;
    /// unowned sessions (legacy loopback, channels, cron) are system data visible only to
    /// admin-equivalent callers — an identified viewer gets neither other accounts' sessions
    /// nor unowned ones.
    /// </summary>
    public static bool CanRead(Session session, string? viewerOwnerAccountId)
        => CanRead(session.OwnerAccountId, viewerOwnerAccountId);

    /// <summary>Owner-only overload for hit-level checks that only have the session's owner id.</summary>
    public static bool CanRead(string? sessionOwnerAccountId, string? viewerOwnerAccountId)
        => string.IsNullOrWhiteSpace(viewerOwnerAccountId)
           || string.Equals(sessionOwnerAccountId, viewerOwnerAccountId, StringComparison.Ordinal);
}
