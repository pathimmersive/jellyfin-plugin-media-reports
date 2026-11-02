using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaReports.Storage;

/// <summary>
/// Reads and writes reports, the issues they are grouped into, and their history.
/// Every change runs in one transaction.
/// </summary>
public sealed class ReportStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> JsonColumns = new(StringComparer.Ordinal)
    {
        "snapshot", "provider_ids", "series_provider_ids",
    };

    private readonly ReportDatabase _db;
    private readonly TimeProvider _time;
    private readonly Func<int> _retentionDays;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportStore"/> class.
    /// </summary>
    /// <param name="db">The database.</param>
    /// <param name="time">The clock.</param>
    /// <param name="retentionDays">Returns the current retention period in days; read on every purge.</param>
    public ReportStore(ReportDatabase db, TimeProvider time, Func<int> retentionDays)
    {
        _db = db;
        _time = time;
        _retentionDays = retentionDays;
    }

    /// <summary>
    /// Gets the database.
    /// </summary>
    public ReportDatabase Database => _db;

    /// <summary>
    /// Stores a report in the open issue for its item and reason, or in a new issue.
    /// </summary>
    /// <param name="report">The report.</param>
    /// <returns>The report and issue ids.</returns>
    public FiledReport AddReport(NewReport report)
    {
        var now = Now();
        using var connection = _db.OpenConnection();
        using var transaction = Transaction.Begin(connection, immediate: true);

        var issueId = Scalar<long?>(connection, "SELECT id FROM issue WHERE item_id = $item AND reason_code = $reason AND closed_at IS NULL", ("$item", Id(report.ItemId)), ("$reason", report.ReasonCode));
        if (issueId is null)
        {
            issueId = Scalar<long>(
                connection,
                """
                INSERT INTO issue (item_id, reason_code, status, created_at, updated_at, checked_file_size, checked_file_modified)
                VALUES ($item, $reason, 'New', $now, $now, $size, $modified) RETURNING id
                """,
                ("$item", Id(report.ItemId)),
                ("$reason", report.ReasonCode),
                ("$now", now),
                ("$size", report.FileSize),
                ("$modified", Time(report.FileModified)));
        }
        else
        {
            Execute(connection, "UPDATE issue SET updated_at = $now WHERE id = $id", ("$now", now), ("$id", issueId));
        }

        var snapshot = report.Snapshot;
        var reportId = Scalar<long>(
            connection,
            """
            INSERT INTO report (created_at, reporter_user_id, reporter_name, item_id, item_type, item_name, provider_ids,
                series_id, series_name, series_provider_ids, season_number, episode_number, file_path, file_size, file_modified,
                reason_code, note, issue_id, snapshot, play_method, transcode_reasons, client, device)
            VALUES ($now, $user, $name, $item, $type, $itemName, $providers, $series, $seriesName, $seriesProviders, $season, $episode,
                $path, $size, $modified, $reason, $note, $issue, $snapshot, $method, $reasons, $client, $device)
            RETURNING id
            """,
            ("$now", now),
            ("$user", Id(report.ReporterUserId)),
            ("$name", report.ReporterName),
            ("$item", Id(report.ItemId)),
            ("$type", report.ItemType),
            ("$itemName", report.ItemName),
            ("$providers", JsonSerializer.Serialize(report.ProviderIds)),
            ("$series", report.SeriesId is { } series ? Id(series) : null),
            ("$seriesName", report.SeriesName),
            ("$seriesProviders", JsonSerializer.Serialize(report.SeriesProviderIds)),
            ("$season", report.SeasonNumber),
            ("$episode", report.EpisodeNumber),
            ("$path", report.FilePath),
            ("$size", report.FileSize),
            ("$modified", Time(report.FileModified)),
            ("$reason", report.ReasonCode),
            ("$note", report.Note),
            ("$issue", issueId),
            ("$snapshot", snapshot is null ? null : JsonSerializer.Serialize(snapshot, JsonOptions)),
            ("$method", snapshot?.PlayMethod),
            ("$reasons", snapshot?.Transcoding is { TranscodeReasons.Count: > 0 } t ? string.Join(',', t.TranscodeReasons) : null),
            ("$client", snapshot?.Client),
            ("$device", report.Device ?? snapshot?.DeviceName));

        AddEvent(connection, now, issueId, reportId, report.ReporterUserId, "filed", null);
        transaction.Commit();
        return new FiledReport(reportId, issueId.Value);
    }

    /// <summary>
    /// Changes an issue's status. Closing records the closed time; reopening clears it and
    /// is refused while a newer issue for the same item and reason is open.
    /// </summary>
    /// <param name="issueId">The issue.</param>
    /// <param name="status">The new status.</param>
    /// <param name="actorUserId">Who changed it, or null for the plugin itself.</param>
    /// <returns>What happened.</returns>
    public StatusChangeResult SetStatus(long issueId, IssueStatus status, Guid? actorUserId)
    {
        using var connection = _db.OpenConnection();
        using var transaction = Transaction.Begin(connection, immediate: true);
        var result = SetStatus(connection, issueId, status, actorUserId, Now(), null);
        transaction.Commit();
        return result;
    }

    /// <summary>
    /// Deletes closed issues, with their reports and events, that closed longer ago than
    /// the retention period. Open issues are never deleted; a period of 0 or less keeps everything.
    /// </summary>
    /// <returns>How many issues were deleted.</returns>
    public int Purge()
    {
        var days = _retentionDays();
        if (days <= 0)
        {
            return 0;
        }

        var now = _time.GetUtcNow();
        using var connection = _db.OpenConnection();
        using var transaction = Transaction.Begin(connection, immediate: true);
        var cutoff = Time(now.AddDays(-days));
        var ids = Column<long>(connection, "SELECT id FROM issue WHERE closed_at IS NOT NULL AND closed_at < $cutoff", ("$cutoff", cutoff));
        if (ids.Count > 0)
        {
            const string Old = "SELECT id FROM issue WHERE closed_at IS NOT NULL AND closed_at < $cutoff";
            Execute(connection, $"DELETE FROM event WHERE issue_id IN ({Old})", ("$cutoff", cutoff));
            Execute(connection, $"DELETE FROM report WHERE issue_id IN ({Old})", ("$cutoff", cutoff));
            Execute(connection, "DELETE FROM issue WHERE closed_at IS NOT NULL AND closed_at < $cutoff", ("$cutoff", cutoff));
            AddEvent(connection, Time(now)!, null, null, null, "purged", string.Create(CultureInfo.InvariantCulture, $"{ids.Count} closed issues older than {days} days"));
        }

        transaction.Commit();
        return ids.Count;
    }

    /// <summary>
    /// Clears a deleted user from their reports and the history, keeping the reports.
    /// </summary>
    /// <param name="userId">The deleted user.</param>
    /// <returns>How many reports were anonymised.</returns>
    public int AnonymiseUser(Guid userId)
    {
        var now = Now();
        var user = Id(userId);
        using var connection = _db.OpenConnection();
        using var transaction = Transaction.Begin(connection, immediate: true);
        var reports = Rows(connection, "SELECT id, issue_id, snapshot FROM report WHERE reporter_user_id = $user", r => (Id: r.GetInt64(0), Issue: r.GetInt64(1), Snapshot: r.IsDBNull(2) ? null : r.GetString(2)), ("$user", user));
        foreach (var report in reports)
        {
            Execute(
                connection,
                "UPDATE report SET reporter_user_id = NULL, reporter_name = NULL, device = NULL, snapshot = $snapshot WHERE id = $id",
                ("$snapshot", WithoutDevice(report.Snapshot)),
                ("$id", report.Id));
            AddEvent(connection, now, report.Issue, report.Id, null, "anonymised", null);
        }

        Execute(connection, "UPDATE event SET actor_user_id = NULL WHERE actor_user_id = $user", ("$user", user));
        Execute(connection, "DELETE FROM admin_seen WHERE user_id = $user", ("$user", user));
        transaction.Commit();
        return reports.Count;
    }

    /// <summary>
    /// Withdraws a reporter's own report while its issue is New. An issue left with no
    /// reports is deleted with its events.
    /// </summary>
    /// <param name="reportId">The report.</param>
    /// <param name="userId">The caller.</param>
    /// <returns>What happened.</returns>
    public Withdrawal Withdraw(long reportId, Guid userId)
    {
        using var connection = _db.OpenConnection();
        using var transaction = Transaction.Begin(connection, immediate: true);
        var found = Rows(
            connection,
            "SELECT r.reporter_user_id, r.issue_id, i.status FROM report r JOIN issue i ON i.id = r.issue_id WHERE r.id = $id",
            r => (User: r.IsDBNull(0) ? null : r.GetString(0), Issue: r.GetInt64(1), Status: r.GetString(2)),
            ("$id", reportId));
        if (found.Count == 0)
        {
            return Withdrawal.NotFound;
        }

        var (user, issueId, status) = found[0];
        if (!string.Equals(user, Id(userId), StringComparison.Ordinal))
        {
            return Withdrawal.NotYours;
        }

        if (Enum.Parse<IssueStatus>(status) != IssueStatus.New)
        {
            return Withdrawal.NotNew;
        }

        Execute(connection, "DELETE FROM report WHERE id = $id", ("$id", reportId));
        if (Scalar<long>(connection, "SELECT COUNT(*) FROM report WHERE issue_id = $issue", ("$issue", issueId)) == 0)
        {
            Execute(connection, "DELETE FROM event WHERE issue_id = $issue", ("$issue", issueId));
            Execute(connection, "DELETE FROM issue WHERE id = $issue", ("$issue", issueId));
        }
        else
        {
            AddEvent(connection, Now(), issueId, reportId, userId, "withdrawn", null);
        }

        transaction.Commit();
        return Withdrawal.Withdrawn;
    }

    /// <summary>
    /// Moves every issue on a replaced item to its replacement. Open issues become
    /// Replaced – verify; an open issue meets the replacement's open issue for the same
    /// reason by joining it.
    /// </summary>
    /// <param name="oldItemId">The removed item.</param>
    /// <param name="newItemId">The item that replaced it.</param>
    /// <returns>How many issues moved.</returns>
    public int MoveItemIssues(Guid oldItemId, Guid newItemId)
    {
        var now = Now();
        var detail = $"Moved to the replaced file: {Id(oldItemId)} → {Id(newItemId)}";
        using var connection = _db.OpenConnection();
        using var transaction = Transaction.Begin(connection, immediate: true);
        var issues = Rows(
            connection,
            "SELECT id, reason_code, closed_at IS NULL FROM issue WHERE item_id = $old ORDER BY id",
            r => (Id: r.GetInt64(0), Reason: r.GetString(1), Open: r.GetBoolean(2)),
            ("$old", Id(oldItemId)));
        foreach (var issue in issues)
        {
            Execute(connection, "UPDATE report SET item_id = $new WHERE issue_id = $id", ("$new", Id(newItemId)), ("$id", issue.Id));
            var target = issue.Open
                ? Scalar<long?>(connection, "SELECT id FROM issue WHERE item_id = $new AND reason_code = $reason AND closed_at IS NULL", ("$new", Id(newItemId)), ("$reason", issue.Reason))
                : null;
            if (target is { } joined)
            {
                Execute(connection, "UPDATE report SET issue_id = $joined WHERE issue_id = $id", ("$joined", joined), ("$id", issue.Id));
                Execute(connection, "UPDATE event SET issue_id = $joined WHERE issue_id = $id", ("$joined", joined), ("$id", issue.Id));
                Execute(connection, "DELETE FROM issue WHERE id = $id", ("$id", issue.Id));
                AddEvent(connection, now, joined, null, null, "relinked", detail);
                SetStatus(connection, joined, IssueStatus.ReplacedVerify, null, now, null);
                continue;
            }

            Execute(connection, "UPDATE issue SET item_id = $new, updated_at = $now WHERE id = $id", ("$new", Id(newItemId)), ("$now", now), ("$id", issue.Id));
            AddEvent(connection, now, issue.Id, null, null, "relinked", detail);
            if (issue.Open)
            {
                SetStatus(connection, issue.Id, IssueStatus.ReplacedVerify, null, now, null);
            }
        }

        transaction.Commit();
        return issues.Count;
    }

    /// <summary>
    /// Records now as when an admin last opened the Reports tab.
    /// </summary>
    /// <param name="adminUserId">The admin.</param>
    public void MarkAdminSeen(Guid adminUserId)
    {
        using var connection = _db.OpenConnection();
        Execute(
            connection,
            "INSERT INTO admin_seen (user_id, seen_at) VALUES ($user, $now) ON CONFLICT (user_id) DO UPDATE SET seen_at = excluded.seen_at",
            ("$user", Id(adminUserId)),
            ("$now", Now()));
    }

    /// <summary>
    /// Gets when an admin last opened the Reports tab.
    /// </summary>
    /// <param name="adminUserId">The admin.</param>
    /// <returns>The time, or null if never.</returns>
    public DateTimeOffset? GetAdminSeen(Guid adminUserId)
    {
        using var connection = _db.OpenConnection();
        return ParseTime(Scalar<string?>(connection, "SELECT seen_at FROM admin_seen WHERE user_id = $user", ("$user", Id(adminUserId))));
    }

    /// <summary>
    /// Gets an issue.
    /// </summary>
    /// <param name="issueId">The issue.</param>
    /// <returns>The issue, or null.</returns>
    public StoredIssue? GetIssue(long issueId)
    {
        using var connection = _db.OpenConnection();
        return Rows(connection, "SELECT * FROM issue WHERE id = $id", ReadIssue, ("$id", issueId)).SingleOrDefault();
    }

    /// <summary>
    /// Gets a report.
    /// </summary>
    /// <param name="reportId">The report.</param>
    /// <returns>The report, or null.</returns>
    public StoredReport? GetReport(long reportId)
    {
        using var connection = _db.OpenConnection();
        return Rows(connection, "SELECT * FROM report WHERE id = $id", ReadReport, ("$id", reportId)).SingleOrDefault();
    }

    /// <summary>
    /// Gets an issue's reports, newest first.
    /// </summary>
    /// <param name="issueId">The issue.</param>
    /// <returns>The reports.</returns>
    public IReadOnlyList<StoredReport> GetIssueReports(long issueId)
    {
        using var connection = _db.OpenConnection();
        return Rows(connection, "SELECT * FROM report WHERE issue_id = $id ORDER BY created_at DESC, id DESC", ReadReport, ("$id", issueId));
    }

    /// <summary>
    /// Gets an issue's history, oldest first.
    /// </summary>
    /// <param name="issueId">The issue.</param>
    /// <returns>The events.</returns>
    public IReadOnlyList<StoredEvent> GetIssueEvents(long issueId)
    {
        using var connection = _db.OpenConnection();
        return Rows(connection, "SELECT * FROM event WHERE issue_id = $id ORDER BY at, id", ReadEvent, ("$id", issueId));
    }

    /// <summary>
    /// Gets a user's reports on an item whose issue is still open, with the issue's status.
    /// </summary>
    /// <param name="userId">The reporter.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The reports, newest first.</returns>
    public IReadOnlyList<(long ReportId, string ReasonCode, IssueStatus Status)> GetOpenReports(Guid userId, Guid itemId)
    {
        using var connection = _db.OpenConnection();
        return Rows(
            connection,
            """
            SELECT r.id, r.reason_code, i.status FROM report r JOIN issue i ON i.id = r.issue_id
            WHERE r.reporter_user_id = $user AND r.item_id = $item AND i.closed_at IS NULL
            ORDER BY r.created_at DESC, r.id DESC
            """,
            r => (r.GetInt64(0), r.GetString(1), Enum.Parse<IssueStatus>(r.GetString(2))),
            ("$user", Id(userId)),
            ("$item", Id(itemId)));
    }

    /// <summary>
    /// Gets how many reports are stored.
    /// </summary>
    /// <returns>The count.</returns>
    public long GetReportCount()
    {
        using var connection = _db.OpenConnection();
        return Scalar<long>(connection, "SELECT COUNT(*) FROM report");
    }

    /// <summary>
    /// Writes a consistent copy of the database with SQLite's online backup, replacing the
    /// previous copy only once the new one is complete. Writes continue while it runs.
    /// </summary>
    public void WriteCopy()
    {
        var partial = _db.CopyPath + ".partial";
        File.Delete(partial);
        using (var source = _db.OpenConnection())
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = partial, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
            Execute(destination, "PRAGMA journal_mode = DELETE");
        }

        File.Move(partial, _db.CopyPath, overwrite: true);
    }

    /// <summary>
    /// Writes every issue, report and event as one JSON document with the schema version.
    /// Changes nothing.
    /// </summary>
    /// <param name="output">Where to write.</param>
    public void Export(Stream output)
    {
        using var connection = _db.OpenConnection();
        using var transaction = Transaction.Begin(connection, immediate: false);
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", ReportDatabase.SchemaVersion);
        writer.WriteString("exportedAt", _time.GetUtcNow());
        foreach (var (name, table) in new[] { ("issues", "issue"), ("reports", "report"), ("events", "event") })
        {
            writer.WriteStartArray(name);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                writer.WriteStartObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var column = reader.GetName(i);
                    writer.WritePropertyName(CamelCase(column));
                    if (reader.IsDBNull(i))
                    {
                        writer.WriteNullValue();
                    }
                    else if (JsonColumns.Contains(column))
                    {
                        writer.WriteRawValue(reader.GetString(i));
                    }
                    else if (reader.GetValue(i) is long number)
                    {
                        writer.WriteNumberValue(number);
                    }
                    else
                    {
                        writer.WriteStringValue(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private StatusChangeResult SetStatus(SqliteConnection connection, long issueId, IssueStatus status, Guid? actorUserId, string now, string? detail)
    {
        var current = Rows(connection, "SELECT status, closed_at, item_id, reason_code FROM issue WHERE id = $id", r => (Status: Enum.Parse<IssueStatus>(r.GetString(0)), ClosedAt: r.IsDBNull(1) ? null : r.GetString(1), Item: r.GetString(2), Reason: r.GetString(3)), ("$id", issueId)).SingleOrDefault();
        if (current == default)
        {
            return new StatusChangeResult(StatusChange.NotFound);
        }

        if (current.Status == status)
        {
            return new StatusChangeResult(StatusChange.Unchanged);
        }

        var closedAt = status.IsClosed() ? current.ClosedAt ?? now : null;
        if (closedAt is null && current.ClosedAt is not null)
        {
            var open = Scalar<long?>(connection, "SELECT id FROM issue WHERE item_id = $item AND reason_code = $reason AND closed_at IS NULL", ("$item", current.Item), ("$reason", current.Reason));
            if (open is not null)
            {
                return new StatusChangeResult(StatusChange.NewerOpenIssue, open);
            }
        }

        Execute(connection, "UPDATE issue SET status = $status, closed_at = $closed, updated_at = $now WHERE id = $id", ("$status", status.ToString()), ("$closed", closedAt), ("$now", now), ("$id", issueId));
        AddEvent(connection, now, issueId, null, actorUserId, "status changed", detail ?? $"{current.Status.DisplayName()} → {status.DisplayName()}");
        return new StatusChangeResult(StatusChange.Changed);
    }

    private static void AddEvent(SqliteConnection connection, string at, long? issueId, long? reportId, Guid? actor, string kind, string? detail)
    {
        Execute(
            connection,
            "INSERT INTO event (at, issue_id, report_id, actor_user_id, kind, detail) VALUES ($at, $issue, $report, $actor, $kind, $detail)",
            ("$at", at),
            ("$issue", issueId),
            ("$report", reportId),
            ("$actor", actor is { } a ? Id(a) : null),
            ("$kind", kind),
            ("$detail", detail));
    }

    private static string? WithoutDevice(string? snapshot)
    {
        if (snapshot is null || JsonNode.Parse(snapshot) is not JsonObject json)
        {
            return snapshot;
        }

        json.Remove("deviceName");
        json.Remove("deviceId");
        return json.ToJsonString();
    }

    private static StoredIssue ReadIssue(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        ItemId = Guid.Parse(r.GetString(r.GetOrdinal("item_id"))),
        ReasonCode = r.GetString(r.GetOrdinal("reason_code")),
        Status = Enum.Parse<IssueStatus>(r.GetString(r.GetOrdinal("status"))),
        AdminNotes = Text(r, "admin_notes"),
        CreatedAt = ParseTime(Text(r, "created_at"))!.Value,
        UpdatedAt = ParseTime(Text(r, "updated_at"))!.Value,
        ClosedAt = ParseTime(Text(r, "closed_at")),
        CheckedFileSize = Number(r, "checked_file_size"),
        CheckedFileModified = ParseTime(Text(r, "checked_file_modified")),
    };

    private static StoredReport ReadReport(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        CreatedAt = ParseTime(Text(r, "created_at"))!.Value,
        ReporterUserId = Text(r, "reporter_user_id") is { } user ? Guid.Parse(user) : null,
        ReporterName = Text(r, "reporter_name"),
        ItemId = Guid.Parse(r.GetString(r.GetOrdinal("item_id"))),
        ItemType = r.GetString(r.GetOrdinal("item_type")),
        ItemName = r.GetString(r.GetOrdinal("item_name")),
        ProviderIds = Map(Text(r, "provider_ids")),
        SeriesId = Text(r, "series_id") is { } series ? Guid.Parse(series) : null,
        SeriesName = Text(r, "series_name"),
        SeriesProviderIds = Map(Text(r, "series_provider_ids")),
        SeasonNumber = (int?)Number(r, "season_number"),
        EpisodeNumber = (int?)Number(r, "episode_number"),
        FilePath = Text(r, "file_path"),
        FileSize = Number(r, "file_size"),
        FileModified = ParseTime(Text(r, "file_modified")),
        ReasonCode = r.GetString(r.GetOrdinal("reason_code")),
        Note = Text(r, "note"),
        IssueId = r.GetInt64(r.GetOrdinal("issue_id")),
        SnapshotJson = Text(r, "snapshot"),
        PlayMethod = Text(r, "play_method"),
        TranscodeReasons = Text(r, "transcode_reasons"),
        Client = Text(r, "client"),
        Device = Text(r, "device"),
        ReporterSeenAt = ParseTime(Text(r, "reporter_seen_at")),
    };

    private static StoredEvent ReadEvent(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        At = ParseTime(Text(r, "at"))!.Value,
        IssueId = Number(r, "issue_id"),
        ReportId = Number(r, "report_id"),
        ActorUserId = Text(r, "actor_user_id") is { } actor ? Guid.Parse(actor) : null,
        Kind = r.GetString(r.GetOrdinal("kind")),
        Detail = Text(r, "detail"),
    };

    private static string? Text(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static long? Number(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    private static IReadOnlyDictionary<string, string> Map(string? json) =>
        string.IsNullOrEmpty(json) ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    private static string CamelCase(string column)
    {
        var parts = column.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private string Now() => Time(_time.GetUtcNow())!;

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string? Time(DateTimeOffset? time) => time?.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(string? text) =>
        text is null ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
        {
            return default!;
        }

        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    private static List<T> Column<T>(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Rows(connection, sql, r => (T)Convert.ChangeType(r.GetValue(0), typeof(T), CultureInfo.InvariantCulture), parameters);

    private static List<T> Rows<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>
    /// A transaction begun with plain SQL, so commands need no transaction object. Immediate
    /// transactions take the write lock at once, so two reports filed together can't both
    /// start an issue. Disposing without a commit rolls back.
    /// </summary>
    private sealed class Transaction : IDisposable
    {
        private readonly SqliteConnection _connection;
        private bool _done;

        private Transaction(SqliteConnection connection)
        {
            _connection = connection;
        }

        public static Transaction Begin(SqliteConnection connection, bool immediate)
        {
            Execute(connection, immediate ? "BEGIN IMMEDIATE" : "BEGIN");
            return new Transaction(connection);
        }

        public void Commit()
        {
            Execute(_connection, "COMMIT");
            _done = true;
        }

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                Execute(_connection, "ROLLBACK");
            }
        }
    }
}
