using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Fetch conversation history/transcript for a session.
/// </summary>
internal sealed class SessionsHistoryTool : IToolWithContext
{
    private readonly SessionManager _sessions;
    private readonly IMemoryStore _store;

    public SessionsHistoryTool(SessionManager sessions, IMemoryStore store)
    {
        _sessions = sessions;
        _store = store;
    }

    public string Name => "sessions_history";
    public string Description => "Fetch the conversation history for a session. Returns recent turns with role and content.";
    public string ParameterSchema => """{"type":"object","properties":{"session_id":{"type":"string","description":"Session ID to fetch history for"},"limit":{"type":"integer","description":"Max turns to return (default 20, max 100)"}},"required":["session_id"]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: sessions_history requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var sessionId = GetString(root, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return "Error: 'session_id' is required.";

        var limit = 20;
        if (root.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(l.GetInt32(), 1, 100);

        // Try active session first, then load from store
        var session = _sessions.TryGetActiveById(sessionId);
        if (session is null)
            session = await _store.GetSessionAsync(sessionId, ct);

        if (session is null || !SessionAccess.CanRead(session, context.Session.OwnerAccountId))
            return $"Session '{sessionId}' not found.";

        var history = session.History;
        if (history.Count == 0)
            return "Session has no conversation history.";

        var start = Math.Max(0, history.Count - limit);
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {sessionId} ({history.Count} total turns, showing last {history.Count - start})");
        sb.AppendLine();

        for (var i = start; i < history.Count; i++)
        {
            var turn = history[i];
            sb.AppendLine($"[{turn.Role}] ({turn.Timestamp:HH:mm:ss})");
            sb.AppendLine(turn.Content);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
