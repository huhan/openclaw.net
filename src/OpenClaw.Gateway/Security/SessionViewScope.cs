using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Endpoints;

namespace OpenClaw.Gateway.Security;

/// <summary>
/// Session visibility rules for operator endpoints:
/// admins — including loopback-open operators — see every session; other
/// operator roles only see sessions they own. Ownership matches when the
/// session's <c>AuthenticatedUserId</c> or its channel <c>SenderId</c> equals
/// the caller's account id.
/// </summary>
internal static class SessionViewScope
{
    public static bool IsAdmin(EndpointHelpers.OperatorAuthorizationResult auth)
        => auth.IsBootstrapAdmin
           || string.Equals(auth.AuthMode, "loopback-open", StringComparison.OrdinalIgnoreCase)
           || OperatorRoleNames.Normalize(auth.Role) == OperatorRoleNames.Admin;

    public static bool CanViewSession(EndpointHelpers.OperatorAuthorizationResult auth, Session session)
        => IsAdmin(auth) || MatchesCaller(auth.AccountId, session.SenderId, session.AuthenticatedUserId);

    public static bool CanViewSummary(EndpointHelpers.OperatorAuthorizationResult auth, SessionSummary summary)
        => IsAdmin(auth) || MatchesCaller(auth.AccountId, summary.SenderId, summary.AuthenticatedUserId);

    private static bool MatchesCaller(string? accountId, string senderId, string? authenticatedUserId)
        => !string.IsNullOrWhiteSpace(accountId)
           && (string.Equals(authenticatedUserId, accountId, StringComparison.Ordinal)
               || string.Equals(senderId, accountId, StringComparison.Ordinal));
}
