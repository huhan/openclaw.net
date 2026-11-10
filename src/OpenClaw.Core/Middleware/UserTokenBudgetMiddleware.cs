using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Middleware;

/// <summary>
/// Enforces a per-user daily token budget across sessions. Where
/// <see cref="TokenBudgetMiddleware"/> gates a single session, this gate rejects
/// callers whose day-wide usage has reached the quota, so starting a new session
/// no longer resets the allowance — and every new day does, because the ledger
/// buckets usage per calendar day.
/// Identity resolution, ledger lookup, and admin exemption are injected as
/// delegates so this middleware stays free of gateway-level identity services.
/// </summary>
public sealed class UserTokenBudgetMiddleware : IMessageMiddleware
{
    private readonly long _maxTokensPerUser;
    private readonly Func<MessageContext, string?> _userIdResolver;
    private readonly Func<string, long> _usageLookup;
    private readonly Func<string, bool> _exemptCheck;
    private readonly ILogger? _logger;

    public string Name => "UserTokenBudget";

    /// <param name="maxTokensPerUser">Max tokens (input + output) per user per calendar day. 0 = unlimited.</param>
    /// <param name="userIdResolver">Resolves the budgeting identity for a message; empty result skips the check.</param>
    /// <param name="usageLookup">Returns the user's usage so far today, in tokens.</param>
    /// <param name="exemptCheck">Returns true for users exempt from budgeting (e.g. admins).</param>
    public UserTokenBudgetMiddleware(
        long maxTokensPerUser,
        Func<MessageContext, string?> userIdResolver,
        Func<string, long> usageLookup,
        Func<string, bool> exemptCheck,
        ILogger? logger = null)
    {
        _maxTokensPerUser = maxTokensPerUser;
        _userIdResolver = userIdResolver ?? throw new ArgumentNullException(nameof(userIdResolver));
        _usageLookup = usageLookup ?? throw new ArgumentNullException(nameof(usageLookup));
        _exemptCheck = exemptCheck ?? throw new ArgumentNullException(nameof(exemptCheck));
        _logger = logger;
    }

    public ValueTask InvokeAsync(MessageContext context, Func<ValueTask> next, CancellationToken ct)
    {
        if (_maxTokensPerUser <= 0)
            return next();

        var userId = _userIdResolver(context);
        if (string.IsNullOrWhiteSpace(userId))
            return next();

        if (_exemptCheck(userId))
            return next();

        var used = _usageLookup(userId);
        var projected = used + EstimateTokenCount(context.Text);
        if (projected >= _maxTokensPerUser)
        {
            _logger?.LogWarning("User token budget exceeded for {UserId} (used {Used}, projected {Projected}, max {Max}).",
                userId, used, projected, _maxTokensPerUser);
            context.ShortCircuit(
                $"This account has reached its daily token budget (used {used:N0}, projected {projected:N0}, " +
                $"limit {_maxTokensPerUser:N0} tokens). The quota resets tomorrow; " +
                "contact an administrator to reset it now or raise the limit.");
            return ValueTask.CompletedTask;
        }

        return next();
    }

    /// <summary>
    /// Rough pre-flight estimate (~4 characters per token) so oversized inputs are
    /// rejected before any model call. The precise per-turn admission remains the
    /// session-level <c>EnableEstimatedTokenAdmissionControl</c> path.
    /// </summary>
    internal static long EstimateTokenCount(string? text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
