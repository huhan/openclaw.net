namespace OpenClaw.Core.Models;

/// <summary>
/// Per-user daily token budget. Bound from <c>OpenClaw:UserTokenBudget</c>.
/// Usage is recorded per calendar day (server-local time) in a SQLite ledger, so
/// the quota resets at midnight and survives restarts.
/// </summary>
public sealed class UserTokenBudgetConfig
{
    /// <summary>Master switch. When false, no user-level budgeting or ledger recording happens.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Max tokens (input + output) per user per calendar day, across all sessions.
    /// 0 = unlimited (usage is still recorded for observability).
    /// </summary>
    public long MaxTokensPerUser { get; set; } = 0;

    /// <summary>When true (default), operator accounts with the admin role bypass the budget.</summary>
    public bool ExemptAdmins { get; set; } = true;

    /// <summary>
    /// Optional explicit SQLite file path. Defaults to
    /// <c>&lt;memory storage path&gt;/budgeting/user-token-budget.db</c>.
    /// </summary>
    public string? DatabasePath { get; set; }
}
