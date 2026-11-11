using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Tools;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Ownership scoping for the cross-session agent tools (sessions / session_search /
/// sessions_history): an authenticated session may only see and touch sessions owned
/// by the same account; unowned system/legacy sessions are invisible to identified
/// callers; callers without an owned session keep the unscoped admin-equivalent view.
/// </summary>
public sealed class SessionOwnershipToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;
    private readonly SessionManager _sessions;
    private readonly SessionsTool _sessionsTool;
    private readonly SessionsHistoryTool _historyTool;

    private const string AccA = "opacct_aaaa";
    private const string AccB = "opacct_bbbb";
    private const string SessionAId = "websocket:conn-a";
    private const string SessionBId = "websocket:conn-b";
    private const string UnownedId = "cron:nightly";

    public SessionOwnershipToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
        _sessions = new SessionManager(_store, new GatewayConfig());
        _sessionsTool = new SessionsTool(_sessions, Channel.CreateUnbounded<InboundMessage>().Writer);
        _historyTool = new SessionsHistoryTool(_sessions, _store);

        SeedSessions().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task SeedSessions()
    {
        // aa-owned session with history
        var sessionA = await _sessions.GetOrCreateByIdAsync(SessionAId, "websocket", "conn-a", CancellationToken.None, AccA);
        sessionA.History.Add(new ChatTurn { Role = "user", Content = "apple pie recipe" });
        sessionA.History.Add(new ChatTurn { Role = "assistant", Content = "Here is an apple pie recipe." });

        // bb-owned session with history
        var sessionB = await _sessions.GetOrCreateByIdAsync(SessionBId, "websocket", "conn-b", CancellationToken.None, AccB);
        sessionB.History.Add(new ChatTurn { Role = "user", Content = "secret banana plan" });

        // unowned legacy/system session
        await _sessions.GetOrCreateByIdAsync(UnownedId, "cron", "scheduler", CancellationToken.None);
    }

    private static ToolExecutionContext ContextFor(Session session)
        => new()
        {
            Session = session,
            TurnContext = new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId }
        };

    private ToolExecutionContext ContextA()
        => ContextFor(_sessions.TryGetActiveById(SessionAId)
                      ?? throw new InvalidOperationException("session A missing"));

    private ToolExecutionContext ContextB()
        => ContextFor(_sessions.TryGetActiveById(SessionBId)
                      ?? throw new InvalidOperationException("session B missing"));

    private ToolExecutionContext ContextUnowned()
        => ContextFor(_sessions.TryGetActiveById(UnownedId)
                      ?? throw new InvalidOperationException("unowned session missing"));

    private static async Task<string> CallAsync(SessionsTool tool, object args, ToolExecutionContext context)
        => await tool.ExecuteAsync(JsonSerializer.Serialize(args, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), context, CancellationToken.None);

    // ── SessionAccess.CanRead ───────────────────────────────────────

    [Fact]
    public void CanRead_ViewerWithoutAccount_SeesEverything()
    {
        Assert.True(SessionAccess.CanRead(AccA, viewerOwnerAccountId: null));
        Assert.True(SessionAccess.CanRead(sessionOwnerAccountId: null, viewerOwnerAccountId: null));
    }

    [Fact]
    public void CanRead_IdentifiedViewer_SeesOnlyOwnSessions()
    {
        Assert.True(SessionAccess.CanRead(AccA, AccA));
        Assert.False(SessionAccess.CanRead(AccB, AccA));
        Assert.False(SessionAccess.CanRead(sessionOwnerAccountId: null, viewerOwnerAccountId: AccA));
    }

    // ── sessions tool: list ─────────────────────────────────────────

    [Fact]
    public async Task List_OwnedContext_ShowsOnlyOwnSessions()
    {
        var output = await CallAsync(_sessionsTool, new { action = "list" }, ContextA());

        Assert.Contains("Total Active Sessions: 1", output);
        Assert.Contains(SessionAId, output);
        Assert.DoesNotContain(SessionBId, output);
        Assert.DoesNotContain(UnownedId, output);
    }

    [Fact]
    public async Task List_UnownedContext_KeepsUnscopedView()
    {
        var output = await CallAsync(_sessionsTool, new { action = "list" }, ContextUnowned());

        Assert.Contains(SessionAId, output);
        Assert.Contains(SessionBId, output);
        Assert.Contains(UnownedId, output);
    }

    // ── sessions tool: history ──────────────────────────────────────

    [Fact]
    public async Task History_OwnedContext_ForeignSession_ReturnsNotFound()
    {
        var output = await CallAsync(_sessionsTool, new { action = "history", sessionId = SessionBId }, ContextA());

        Assert.Contains("not found", output);
        Assert.DoesNotContain("banana", output);
    }

    [Fact]
    public async Task History_OwnedContext_OwnSession_ReturnsTurns()
    {
        var output = await CallAsync(_sessionsTool, new { action = "history", sessionId = SessionAId }, ContextA());

        Assert.Contains("apple pie", output);
    }

    // ── sessions tool: send ─────────────────────────────────────────

    [Fact]
    public async Task Send_OwnedContext_ToForeignOwnedSession_Denied()
    {
        var output = await CallAsync(_sessionsTool, new { action = "send", sessionId = SessionBId, message = "hi" }, ContextA());

        Assert.Contains("belongs to another account", output);
    }

    [Fact]
    public async Task Send_OwnedContext_ToUnownedSession_AllowedByDesign()
    {
        var output = await CallAsync(_sessionsTool, new { action = "send", sessionId = UnownedId, message = "hi" }, ContextA());

        Assert.Contains("Message queued", output);
    }

    // ── sessions_history tool ───────────────────────────────────────

    [Fact]
    public async Task SessionsHistoryTool_OwnedContext_ForeignSession_ReturnsNotFound()
    {
        var output = await _historyTool.ExecuteAsync(
            JsonSerializer.Serialize(new { session_id = SessionBId }),
            ContextA(),
            CancellationToken.None);

        Assert.Contains("not found", output);
        Assert.DoesNotContain("banana", output);
    }

    [Fact]
    public async Task SessionsHistoryTool_OwnedContext_OwnSession_ReturnsTurns()
    {
        var output = await _historyTool.ExecuteAsync(
            JsonSerializer.Serialize(new { session_id = SessionAId }),
            ContextA(),
            CancellationToken.None);

        Assert.Contains("apple pie", output);
    }

    [Fact]
    public async Task SessionsHistoryTool_UnownedContext_KeepsUnscopedView()
    {
        var output = await _historyTool.ExecuteAsync(
            JsonSerializer.Serialize(new { session_id = SessionBId }),
            ContextUnowned(),
            CancellationToken.None);

        Assert.Contains("banana", output);
    }
}
