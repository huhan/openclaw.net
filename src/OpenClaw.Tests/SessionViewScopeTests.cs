using OpenClaw.Core.Models;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Security;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Session visibility scoping: admins (bootstrap / admin role / loopback-open)
/// see every session; other operator roles only see sessions they own.
/// </summary>
public sealed class SessionViewScopeTests
{
    private static EndpointHelpers.OperatorAuthorizationResult Auth(
        string role,
        string? accountId,
        string authMode = "account-token",
        bool isBootstrapAdmin = false)
        => new(
            IsAuthorized: true,
            AuthMode: authMode,
            UsedBrowserSession: false,
            BrowserSession: null,
            Role: role,
            AccountId: accountId,
            Username: "tester",
            DisplayName: "Tester",
            IsBootstrapAdmin: isBootstrapAdmin);

    private static Session Session(
        string senderId,
        string? authenticatedUserId = null,
        string channelId = "websocket")
        => new()
        {
            Id = $"websocket:{senderId}",
            ChannelId = channelId,
            SenderId = senderId,
            AuthenticatedUserId = authenticatedUserId
        };

    private static SessionSummary Summary(string senderId, string? authenticatedUserId = null)
        => new()
        {
            Id = $"websocket:{senderId}",
            ChannelId = "websocket",
            SenderId = senderId,
            AuthenticatedUserId = authenticatedUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
            State = SessionState.Active
        };

    [Fact]
    public void AdminRole_SeesEverySession()
    {
        var admin = Auth(OperatorRoleNames.Admin, "acc_admin");
        var stranger = Session(senderId: "someone-else", authenticatedUserId: "acc_other");

        Assert.True(SessionViewScope.IsAdmin(admin));
        Assert.True(SessionViewScope.CanViewSession(admin, stranger));
        Assert.True(SessionViewScope.CanViewSummary(admin, Summary("someone-else", "acc_other")));
    }

    [Fact]
    public void LoopbackOpenOperator_IsAdmin()
    {
        var loopback = Auth(OperatorRoleNames.Admin, accountId: null, authMode: "loopback-open");

        Assert.True(SessionViewScope.IsAdmin(loopback));
        Assert.True(SessionViewScope.CanViewSession(loopback, Session("anyone")));
    }

    [Fact]
    public void BootstrapAdmin_SeesEverySession_EvenWithNoAccount()
    {
        var bootstrap = Auth(OperatorRoleNames.Viewer, accountId: null, isBootstrapAdmin: true);

        Assert.True(SessionViewScope.IsAdmin(bootstrap));
        Assert.True(SessionViewScope.CanViewSession(bootstrap, Session("anyone")));
    }

    [Fact]
    public void NonAdmin_SeesOwnSession_ByAuthenticatedUserId()
    {
        var caller = Auth(OperatorRoleNames.Operator, "acc_1");
        var own = Session(senderId: "conn-abc", authenticatedUserId: "acc_1");

        Assert.True(SessionViewScope.CanViewSession(caller, own));
        Assert.True(SessionViewScope.CanViewSummary(caller, Summary("conn-abc", "acc_1")));
    }

    [Fact]
    public void NonAdmin_SeesOwnSession_BySenderIdMatch()
    {
        var caller = Auth(OperatorRoleNames.Operator, "telegram-12345");
        var own = Session(senderId: "telegram-12345", authenticatedUserId: null, channelId: "telegram");

        Assert.True(SessionViewScope.CanViewSession(caller, own));
    }

    [Fact]
    public void NonAdmin_CannotSeeOtherUsersSessions()
    {
        var caller = Auth(OperatorRoleNames.Operator, "acc_1");
        var strangerByAuth = Session(senderId: "conn-xyz", authenticatedUserId: "acc_2");
        var strangerBySender = Session(senderId: "telegram-99999", authenticatedUserId: null);

        Assert.False(SessionViewScope.CanViewSession(caller, strangerByAuth));
        Assert.False(SessionViewScope.CanViewSession(caller, strangerBySender));
        Assert.False(SessionViewScope.CanViewSummary(caller, Summary("conn-xyz", "acc_2")));
    }

    [Fact]
    public void NonAdmin_WithoutAccountId_SeesNothing()
    {
        var caller = Auth(OperatorRoleNames.Viewer, accountId: null);

        Assert.False(SessionViewScope.CanViewSession(caller, Session("acc_1")));
        Assert.False(SessionViewScope.CanViewSummary(caller, Summary("acc_1")));
    }

    [Fact]
    public void ViewerRole_IsNotAdmin()
    {
        var viewer = Auth(OperatorRoleNames.Viewer, "acc_viewer");

        Assert.False(SessionViewScope.IsAdmin(viewer));
    }
}
