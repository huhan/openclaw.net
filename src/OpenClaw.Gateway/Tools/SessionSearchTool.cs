using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Tools;

internal sealed class SessionSearchTool : IToolWithContext
{
    private readonly ISessionSearchStore _searchStore;
    private readonly SessionManager _sessions;

    public SessionSearchTool(ISessionSearchStore searchStore, SessionManager sessions)
    {
        _searchStore = searchStore;
        _sessions = sessions;
    }

    public string Name => "session_search";
    public string Description => "Search conversation history across sessions with snippets and relevance ranking. Only sessions owned by the current account are searched.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "text":{"type":"string"},
        "channel_id":{"type":"string"},
        "sender_id":{"type":"string"},
        "limit":{"type":"integer","minimum":1,"maximum":100},
        "snippet_length":{"type":"integer","minimum":40,"maximum":800}
      },
      "required":["text"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: session_search requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var text = GetString(root, "text");
        if (string.IsNullOrWhiteSpace(text))
            return "Error: text is required.";

        // Ownership lens: the executing session's owner decides which hits are readable.
        // Unowned executing sessions (bootstrap CLI, system flows) keep the unscoped view;
        // identified sessions never see other accounts' sessions or unowned/legacy ones.
        var viewerOwnerAccountId = context.Session.OwnerAccountId;

        var result = await _searchStore.SearchSessionsAsync(new SessionSearchQuery
        {
            Text = text,
            ChannelId = GetString(root, "channel_id"),
            SenderId = GetString(root, "sender_id"),
            Limit = GetInt(root, "limit") ?? 10,
            SnippetLength = GetInt(root, "snippet_length") ?? 180
        }, ct);

        if (result.Items.Count == 0)
            return $"No session hits found for '{text}'.";

        var sb = new StringBuilder();
        foreach (var hit in result.Items)
        {
            if (!SessionAccess.CanRead(await ResolveOwnerAccountIdAsync(hit.SessionId, ct), viewerOwnerAccountId))
                continue;
            sb.AppendLine($"{hit.SessionId} {hit.Role} score={hit.Score:0.00} :: {hit.Snippet}");
        }

        var output = sb.ToString().TrimEnd();
        return output.Length == 0
            ? $"No session hits found for '{text}'."
            : output;
    }

    private async ValueTask<string?> ResolveOwnerAccountIdAsync(string sessionId, CancellationToken ct)
    {
        var active = _sessions.TryGetActiveById(sessionId);
        if (active is not null)
            return active.OwnerAccountId;

        var persisted = await _sessions.LoadAsync(sessionId, ct);
        return persisted?.OwnerAccountId;
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? GetInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;
}
