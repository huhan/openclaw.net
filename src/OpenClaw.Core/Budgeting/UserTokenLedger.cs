using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Budgeting;

/// <summary>Per-user token usage snapshot for one calendar day.</summary>
public sealed record UserTokenUsageSnapshot(
    string UserId,
    long InputTokens,
    long OutputTokens,
    long TurnCount,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>Input + output tokens, matching session budget semantics (cache tokens excluded).</summary>
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// SQLite-backed ledger of token usage per user identity (account id, or channel
/// sender id when the caller is unauthenticated), bucketed by calendar day so
/// quotas reset naturally at midnight: every read and write targets the day key
/// of "now", so a new day starts from zero without any reset job. Rows older
/// than <paramref name="dayRetentionDays"/> are pruned when the ledger opens.
/// Writes are serialized under a lock; one short-lived connection per operation
/// keeps the type safe to share across the inbound workers and turn observers.
/// </summary>
public sealed class UserTokenLedger
{
    private const int DefaultDayRetentionDays = 30;

    private readonly string _dbPath;
    private readonly int _dayRetentionDays;
    private readonly object _gate = new();
    private readonly ILogger? _logger;

    public UserTokenLedger(string dbPath, ILogger? logger = null, int dayRetentionDays = DefaultDayRetentionDays)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _dayRetentionDays = dayRetentionDays;
        _logger = logger;

        var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        Initialize();
        PruneOlderThan(_dayRetentionDays);
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Cache = SqliteCacheMode.Shared,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    /// <summary>Day bucket key in server-local time, so "a day" matches the operator's calendar.</summary>
    internal static string GetDayKey(DateTimeOffset timestamp)
        => timestamp.LocalDateTime.Date.ToString("yyyy-MM-dd");

    private void Initialize()
    {
        lock (_gate)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            if (NeedsDayColumnMigration(conn))
                MigrateLegacySchema(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS user_token_usage (
                    user_id        TEXT    NOT NULL,
                    day            TEXT    NOT NULL,
                    input_tokens   INTEGER NOT NULL DEFAULT 0,
                    output_tokens  INTEGER NOT NULL DEFAULT 0,
                    turn_count     INTEGER NOT NULL DEFAULT 0,
                    first_seen_utc TEXT    NOT NULL,
                    updated_utc    TEXT    NOT NULL,
                    PRIMARY KEY (user_id, day)
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private static bool NeedsDayColumnMigration(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(user_token_usage)";
        using var reader = cmd.ExecuteReader();
        var hasDay = false;
        var hasTable = false;
        while (reader.Read())
        {
            hasTable = true;
            if (string.Equals(reader.GetString(1), "day", StringComparison.OrdinalIgnoreCase))
                hasDay = true;
        }
        return hasTable && !hasDay;
    }

    /// <summary>
    /// Migrates the short-lived v1 schema (lifetime totals keyed by user only) to
    /// day-bucketed rows, carrying each legacy row into the calendar day of its
    /// last update so existing quotas keep their spend history.
    /// </summary>
    private static void MigrateLegacySchema(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                ALTER TABLE user_token_usage RENAME TO user_token_usage_v1;
                CREATE TABLE user_token_usage (
                    user_id        TEXT    NOT NULL,
                    day            TEXT    NOT NULL,
                    input_tokens   INTEGER NOT NULL DEFAULT 0,
                    output_tokens  INTEGER NOT NULL DEFAULT 0,
                    turn_count     INTEGER NOT NULL DEFAULT 0,
                    first_seen_utc TEXT    NOT NULL,
                    updated_utc    TEXT    NOT NULL,
                    PRIMARY KEY (user_id, day)
                );
                INSERT INTO user_token_usage (user_id, day, input_tokens, output_tokens, turn_count, first_seen_utc, updated_utc)
                SELECT user_id, substr(updated_utc, 1, 10), input_tokens, output_tokens, turn_count, first_seen_utc, updated_utc
                FROM user_token_usage_v1;
                DROP TABLE user_token_usage_v1;
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Atomically adds one turn's usage to the user's current day and returns that day's snapshot.</summary>
    public UserTokenUsageSnapshot AddUsage(string userId, long inputTokens, long outputTokens, DateTimeOffset? asOf = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId must be set.", nameof(userId));
        if (inputTokens < 0) inputTokens = 0;
        if (outputTokens < 0) outputTokens = 0;

        var now = (asOf ?? DateTimeOffset.UtcNow).ToString("O");
        var day = GetDayKey(asOf ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var upsert = conn.CreateCommand();
            upsert.CommandText = """
                INSERT INTO user_token_usage (user_id, day, input_tokens, output_tokens, turn_count, first_seen_utc, updated_utc)
                VALUES ($id, $day, $in, $out, 1, $now, $now)
                ON CONFLICT(user_id, day) DO UPDATE SET
                    input_tokens  = input_tokens  + $in,
                    output_tokens = output_tokens + $out,
                    turn_count    = turn_count    + 1,
                    updated_utc   = $now
                """;
            upsert.Parameters.AddWithValue("$id", userId);
            upsert.Parameters.AddWithValue("$day", day);
            upsert.Parameters.AddWithValue("$in", inputTokens);
            upsert.Parameters.AddWithValue("$out", outputTokens);
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();
        }

        var snapshot = GetUsage(userId, asOf);
        _logger?.LogDebug("User token ledger: {UserId} day {Day} now at {Total} tokens ({In} in / {Out} out).",
            userId, day, snapshot.TotalTokens, snapshot.InputTokens, snapshot.OutputTokens);
        return snapshot;
    }

    /// <summary>Returns the user's usage for the calendar day of <paramref name="asOf"/> (defaults to now), zeroed when absent.</summary>
    public UserTokenUsageSnapshot GetUsage(string userId, DateTimeOffset? asOf = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new UserTokenUsageSnapshot(userId ?? string.Empty, 0, 0, 0, DateTimeOffset.UtcNow);

        var day = GetDayKey(asOf ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT input_tokens, output_tokens, turn_count, updated_utc
                FROM user_token_usage WHERE user_id = $id AND day = $day
                """;
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.Parameters.AddWithValue("$day", day);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return new UserTokenUsageSnapshot(userId, 0, 0, 0, DateTimeOffset.UtcNow);
            return new UserTokenUsageSnapshot(
                userId,
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3)));
        }
    }

    /// <summary>Lists today's usage for every tracked user, most recently active first.</summary>
    public IReadOnlyList<UserTokenUsageSnapshot> ListUsage(DateTimeOffset? asOf = null)
    {
        var day = GetDayKey(asOf ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT user_id, input_tokens, output_tokens, turn_count, updated_utc
                FROM user_token_usage WHERE day = $day ORDER BY updated_utc DESC
                """;
            cmd.Parameters.AddWithValue("$day", day);
            using var reader = cmd.ExecuteReader();
            var items = new List<UserTokenUsageSnapshot>();
            while (reader.Read())
            {
                items.Add(new UserTokenUsageSnapshot(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    DateTimeOffset.Parse(reader.GetString(4))));
            }
            return items;
        }
    }

    /// <summary>Deletes all of a user's day rows (full quota release). Returns false when the user had no record.</summary>
    public bool Reset(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        lock (_gate)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM user_token_usage WHERE user_id = $id";
            cmd.Parameters.AddWithValue("$id", userId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Deletes day rows older than the retention window, so the ledger stays small.</summary>
    public int PruneOlderThan(int retentionDays, DateTimeOffset? asOf = null)
    {
        var cutoff = GetDayKey((asOf ?? DateTimeOffset.UtcNow).LocalDateTime.Date.AddDays(-retentionDays));
        lock (_gate)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM user_token_usage WHERE day < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }
}
