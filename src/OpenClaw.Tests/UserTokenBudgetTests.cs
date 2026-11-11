using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Budgeting;
using OpenClaw.Core.Middleware;
using Xunit;

namespace OpenClaw.Tests;

public sealed class UserTokenBudgetTests : IDisposable
{
    private readonly string _tempDir;

    public UserTokenBudgetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private UserTokenLedger CreateLedger(int dayRetentionDays = 30)
        => new(
            Path.Combine(_tempDir, "budgeting", "user-token-budget.db"),
            NullLogger<UserTokenLedger>.Instance,
            dayRetentionDays);

    private string DbPath(string fileName) => Path.Combine(_tempDir, "budgeting", fileName);

    /// <summary>Fixed noon-UTC instants whose local calendar date equals their UTC date for any realistic timezone.</summary>
    private static readonly DateTimeOffset Day1 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day9 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    // ── Ledger ──────────────────────────────────────────────────────

    [Fact]
    public void AddUsage_AccumulatesWithinTheSameDay()
    {
        var ledger = CreateLedger();

        ledger.AddUsage("acc_1", inputTokens: 100, outputTokens: 20, asOf: Day1);
        var snapshot = ledger.AddUsage("acc_1", inputTokens: 50, outputTokens: 10, asOf: Day1);

        Assert.Equal(150, snapshot.InputTokens);
        Assert.Equal(30, snapshot.OutputTokens);
        Assert.Equal(180, snapshot.TotalTokens);
        Assert.Equal(2, snapshot.TurnCount);
    }

    [Fact]
    public void DailyQuota_NewDayStartsFromZero_WithoutAnyResetJob()
    {
        var ledger = CreateLedger();
        ledger.AddUsage("acc_1", 900, 100, asOf: Day1);

        // Next calendar day: the same user reads as zero and may spend again.
        var nextDay = ledger.GetUsage("acc_1", asOf: Day2);
        Assert.Equal(0, nextDay.TotalTokens);

        var spent = ledger.AddUsage("acc_1", 40, 10, asOf: Day2);
        Assert.Equal(50, spent.TotalTokens);

        // The previous day's row is untouched history.
        Assert.Equal(1000, ledger.GetUsage("acc_1", asOf: Day1).TotalTokens);
    }

    [Fact]
    public void DailyQuota_DayBucketsAreIndependentPerUser()
    {
        var ledger = CreateLedger();
        ledger.AddUsage("acc_1", 500, 100, asOf: Day1);
        ledger.AddUsage("acc_2", 10, 5, asOf: Day1);
        ledger.AddUsage("acc_2", 10, 5, asOf: Day2);

        Assert.Equal(600, ledger.GetUsage("acc_1", asOf: Day1).TotalTokens);
        Assert.Equal(15, ledger.GetUsage("acc_2", asOf: Day1).TotalTokens);
        Assert.Equal(15, ledger.GetUsage("acc_2", asOf: Day2).TotalTokens);
        Assert.Equal(0, ledger.GetUsage("acc_3", asOf: Day1).TotalTokens);
    }

    [Fact]
    public void GetUsage_MissingUser_ReturnsZeroedSnapshot()
    {
        var ledger = CreateLedger();

        var snapshot = ledger.GetUsage("nobody", asOf: Day1);

        Assert.Equal("nobody", snapshot.UserId);
        Assert.Equal(0, snapshot.TotalTokens);
        Assert.Equal(0, snapshot.TurnCount);
    }

    [Fact]
    public void AddUsage_SurvivesReopen_SqlitePersistence()
    {
        var first = new UserTokenLedger(DbPath("persist.db"), NullLogger<UserTokenLedger>.Instance);
        first.AddUsage("acc_1", 1234, 567, asOf: Day1);

        var reopened = new UserTokenLedger(DbPath("persist.db"), NullLogger<UserTokenLedger>.Instance);
        var snapshot = reopened.GetUsage("acc_1", asOf: Day1);

        Assert.Equal(1234, snapshot.InputTokens);
        Assert.Equal(567, snapshot.OutputTokens);
    }

    [Fact]
    public void Reset_ClearsAllDayRows_AndReportsMissingUsers()
    {
        var ledger = CreateLedger();
        ledger.AddUsage("acc_1", 10, 10, asOf: Day1);
        ledger.AddUsage("acc_1", 10, 10, asOf: Day2);
        ledger.AddUsage("acc_2", 10, 10, asOf: Day1);

        Assert.True(ledger.Reset("acc_1"));
        Assert.Equal(0, ledger.GetUsage("acc_1", asOf: Day1).TotalTokens);
        Assert.Equal(0, ledger.GetUsage("acc_1", asOf: Day2).TotalTokens);
        Assert.Equal(20, ledger.GetUsage("acc_2", asOf: Day1).TotalTokens);
        Assert.False(ledger.Reset("acc_1"));
    }

    [Fact]
    public void PruneOlderThan_DeletesOldDays_KeepsRecentOnes()
    {
        // 7-day retention: Day1 (2026-11-10) is 8+ days before Day9 (2026-11-18) → pruned.
        var ledger = CreateLedger(dayRetentionDays: 7);
        ledger.AddUsage("acc_1", 100, 10, asOf: Day1);
        ledger.AddUsage("acc_1", 50, 5, asOf: Day2);

        var pruned = ledger.PruneOlderThan(7, asOf: Day9);

        Assert.True(pruned > 0);
        Assert.Equal(0, ledger.GetUsage("acc_1", asOf: Day1).TotalTokens);
        Assert.Equal(55, ledger.GetUsage("acc_1", asOf: Day2).TotalTokens);
    }

    [Fact]
    public void OpenLedger_LegacyLifetimeSchema_MigratesIntoLastUpdateDay()
    {
        // Simulate the pre-daily schema: totals keyed by user only, no day column.
        var dbPath = DbPath("legacy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE user_token_usage (
                    user_id        TEXT    NOT NULL PRIMARY KEY,
                    input_tokens   INTEGER NOT NULL DEFAULT 0,
                    output_tokens  INTEGER NOT NULL DEFAULT 0,
                    turn_count     INTEGER NOT NULL DEFAULT 0,
                    first_seen_utc TEXT    NOT NULL,
                    updated_utc    TEXT    NOT NULL
                );
                INSERT INTO user_token_usage VALUES ('acc_1', 1234, 567, 3, '2026-11-10T12:00:00.0000000Z', '2026-11-10T12:00:00.0000000Z');
                """;
            cmd.ExecuteNonQuery();
        }

        var ledger = new UserTokenLedger(dbPath, NullLogger<UserTokenLedger>.Instance);
        var snapshot = ledger.GetUsage("acc_1", asOf: Day1);

        Assert.Equal(1234, snapshot.InputTokens);
        Assert.Equal(567, snapshot.OutputTokens);
        Assert.Equal(3, snapshot.TurnCount);
    }

    // ── Middleware ──────────────────────────────────────────────────

    private static MessageContext CreateContext(string text = "hello", string senderId = "acc_1")
        => new()
        {
            ChannelId = "websocket",
            SenderId = senderId,
            Text = text,
            SessionId = "websocket:acc_1"
        };

    [Fact]
    public async Task Invoke_OverBudget_ShortCircuitsWithQuotaMessage()
    {
        var ledger = CreateLedger();
        ledger.AddUsage("acc_1", 900, 100, asOf: Day1);
        var middleware = new UserTokenBudgetMiddleware(
            maxTokensPerUser: 1_000,
            ctx => ctx.SenderId,
            // Pin the lookup to the seeded day: production wires GetUsage() to "now",
            // which after midnight correctly reads a fresh zeroed bucket.
            userId => ledger.GetUsage(userId, Day1).TotalTokens,
            userId => false,
            NullLogger.Instance);
        var context = CreateContext("hi");
        var nextCalled = false;

        await middleware.InvokeAsync(context, () => { nextCalled = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.True(context.IsShortCircuited);
        Assert.Contains("token budget", context.ShortCircuitResponse);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invoke_UnderBudget_ContinuesPipeline()
    {
        var ledger = CreateLedger();
        ledger.AddUsage("acc_1", 100, 50, asOf: Day1);
        var middleware = new UserTokenBudgetMiddleware(
            1_000,
            ctx => ctx.SenderId,
            userId => ledger.GetUsage(userId, Day1).TotalTokens,
            userId => false,
            NullLogger.Instance);
        var context = CreateContext("short");
        var nextCalled = false;

        await middleware.InvokeAsync(context, () => { nextCalled = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.False(context.IsShortCircuited);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_OversizedInput_RejectedBeforeModelCall_EvenWhenLedgerBelowQuota()
    {
        var ledger = CreateLedger();
        ledger.AddUsage("acc_1", 10, 0, asOf: Day1);
        var middleware = new UserTokenBudgetMiddleware(
            1_000,
            ctx => ctx.SenderId,
            userId => ledger.GetUsage(userId, Day1).TotalTokens,
            userId => false,
            NullLogger.Instance);
        var context = CreateContext(new string('a', 5_000));
        var nextCalled = false;

        await middleware.InvokeAsync(context, () => { nextCalled = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.True(context.IsShortCircuited);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invoke_ExemptUser_AlwaysContinues()
    {
        var ledger = CreateLedger();
        ledger.AddUsage("admin_1", 9_999, 9_999, asOf: Day1);
        var middleware = new UserTokenBudgetMiddleware(
            1_000,
            ctx => ctx.SenderId,
            userId => ledger.GetUsage(userId).TotalTokens,
            userId => userId == "admin_1",
            NullLogger.Instance);
        var context = CreateContext(senderId: "admin_1");
        var nextCalled = false;

        await middleware.InvokeAsync(context, () => { nextCalled = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.False(context.IsShortCircuited);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_ZeroLimit_IsUnlimited()
    {
        var ledger = CreateLedger();
        ledger.AddUsage("acc_1", 9_999, 9_999, asOf: Day1);
        var middleware = new UserTokenBudgetMiddleware(
            0,
            ctx => ctx.SenderId,
            userId => ledger.GetUsage(userId).TotalTokens,
            userId => false,
            NullLogger.Instance);
        var context = CreateContext(senderId: "acc_1");
        var nextCalled = false;

        await middleware.InvokeAsync(context, () => { nextCalled = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.False(context.IsShortCircuited);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_UnresolvableUserId_ContinuesPipeline()
    {
        var middleware = new UserTokenBudgetMiddleware(
            1_000,
            _ => null,
            _ => 0,
            _ => false,
            NullLogger.Instance);
        var context = CreateContext();
        var nextCalled = false;

        await middleware.InvokeAsync(context, () => { nextCalled = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.False(context.IsShortCircuited);
        Assert.True(nextCalled);
    }
}
