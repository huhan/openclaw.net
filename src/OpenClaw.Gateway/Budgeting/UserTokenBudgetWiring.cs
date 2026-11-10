using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Budgeting;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Budgeting;

/// <summary>
/// Records each model turn's usage into the SQLite user ledger. The budgeting
/// identity is the session's <c>AuthenticatedUserId</c> when present (account
/// token / OIDC / browser session), falling back to the channel sender id.
/// Exempt users (admins, loopback operators) are never recorded.
/// </summary>
internal sealed class UserTokenLedgerTurnObserver : ITurnTokenUsageObserver
{
    private readonly UserTokenLedger _ledger;
    private readonly SessionManager _sessions;
    private readonly Func<string, bool> _isExempt;
    private readonly ILogger? _logger;

    public UserTokenLedgerTurnObserver(
        UserTokenLedger ledger,
        SessionManager sessions,
        Func<string, bool> isExempt,
        ILogger? logger = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _isExempt = isExempt ?? throw new ArgumentNullException(nameof(isExempt));
        _logger = logger;
    }

    public void RecordTurn(TurnTokenUsageRecord record)
    {
        var session = _sessions.TryGetActiveById(record.SessionId);
        var userId = !string.IsNullOrWhiteSpace(session?.AuthenticatedUserId)
            ? session.AuthenticatedUserId
            : session?.SenderId;
        if (string.IsNullOrWhiteSpace(userId))
            return;

        if (_isExempt(userId))
            return;

        _ledger.AddUsage(userId, record.InputTokens, record.OutputTokens);
    }
}

/// <summary>DI composition for the user-level token budget feature.</summary>
internal static class UserTokenBudgetWiring
{
    /// <param name="loopbackOpenAuth">
    /// True when the gateway runs in loopback-open mode (no auth enforced): every
    /// caller is the local machine owner and is treated as an exempt admin.
    /// </param>
    public static IServiceCollection AddUserTokenBudget(
        this IServiceCollection services,
        GatewayConfig config,
        bool loopbackOpenAuth)
    {
        if (!config.UserTokenBudget.Enabled)
            return services;

        var dbPath = ResolveDatabasePath(config);

        services.AddSingleton(sp => new UserTokenLedger(
            dbPath,
            sp.GetRequiredService<ILogger<UserTokenLedger>>()));

        services.AddSingleton(sp => new UserTokenLedgerTurnObserver(
            sp.GetRequiredService<UserTokenLedger>(),
            sp.GetRequiredService<SessionManager>(),
            userId => IsExempt(config, loopbackOpenAuth, sp, userId),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("UserTokenLedgerTurnObserver")));

        services.AddSingleton(sp =>
        {
            var sessionManager = sp.GetRequiredService<SessionManager>();
            var ledger = sp.GetRequiredService<UserTokenLedger>();
            return new UserTokenBudgetMiddleware(
                config.UserTokenBudget.MaxTokensPerUser,
                ctx =>
                {
                    var session = string.IsNullOrWhiteSpace(ctx.SessionId)
                        ? null
                        : sessionManager.TryGetActiveById(ctx.SessionId);
                    return !string.IsNullOrWhiteSpace(session?.AuthenticatedUserId)
                        ? session.AuthenticatedUserId
                        : ctx.SenderId;
                },
                userId => ledger.GetUsage(userId).TotalTokens,
                userId => IsExempt(config, loopbackOpenAuth, sp, userId),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("UserTokenBudget"));
        });

        return services;
    }

    private static bool IsExempt(GatewayConfig config, bool loopbackOpenAuth, IServiceProvider sp, string userId)
    {
        if (loopbackOpenAuth)
            return true;

        if (!config.UserTokenBudget.ExemptAdmins)
            return false;

        var accounts = sp.GetService<OperatorAccountService>();
        var account = accounts?.Get(userId)?.Account;
        return account is { Enabled: true }
            && OperatorRoleNames.Normalize(account.Role) == OperatorRoleNames.Admin;
    }

    private static string ResolveDatabasePath(GatewayConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.UserTokenBudget.DatabasePath))
            return config.UserTokenBudget.DatabasePath!;

        return Path.Combine(
            Path.GetFullPath(config.Memory.StoragePath),
            "budgeting",
            "user-token-budget.db");
    }
}
