using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Playback;
using Jellyfin.Plugin.MediaReports.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class StorageTests : IDisposable
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Movie = Guid.NewGuid();
    private static readonly Guid OtherMovie = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private int _retentionDays = 365;

    public StorageTests()
    {
        Directory.CreateDirectory(_dir);
    }

    private string DbPath => Path.Combine(_dir, "media-reports.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FreshInstall_CreatesTablesAtCurrentVersion()
    {
        var db = Open();

        Assert.True(File.Exists(DbPath));
        Assert.True(db.IsAvailable);
        Assert.Equal(ReportDatabase.SchemaVersion, UserVersion());
        var tables = Query("SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("report", tables);
        Assert.Contains("issue", tables);
        Assert.Contains("event", tables);
        Assert.Contains("admin_seen", tables);
    }

    [Fact]
    public void OlderVersion_IsMigratedAndKeepsReports()
    {
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            Exec(connection, ReportDatabase.Migrations[0]);
            Exec(connection, """
                INSERT INTO issue (id, item_id, reason_code, status, admin_notes, resolution_text, created_at, updated_at)
                VALUES (1, '0f8fad5b-d9cb-469f-a165-70867728950e', 'stutter', 'New', NULL, 'gone', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
                INSERT INTO report (created_at, reporter_user_id, reporter_name, item_id, item_type, item_name, reason_code, issue_id, snapshot, client, device)
                VALUES ('2026-01-01T00:00:00.000Z', '7c9e6679-7425-40de-944b-e07fc1f90ae7', 'alice', '0f8fad5b-d9cb-469f-a165-70867728950e', 'Movie', 'Old Movie', 'stutter', 1, '{"schemaVersion":1,"client":"Jellyfin Web"}', 'Jellyfin Web', 'Laptop');
                PRAGMA user_version = 1;
                """);
        }

        var db = Open();

        Assert.True(db.IsAvailable);
        Assert.Equal(ReportDatabase.SchemaVersion, UserVersion());
        var store = Store(db);
        var report = Assert.Single(store.GetIssueReports(1));
        Assert.Equal("Old Movie", report.ItemName);
        Assert.Equal(1, JsonNode.Parse(report.SnapshotJson!)!["schemaVersion"]!.GetValue<int>());
        Assert.DoesNotContain("resolution_text", Query("SELECT name FROM pragma_table_info('issue')"));
    }

    [Fact]
    public void NewerVersion_LeavesTheFileAloneAndIsUnavailable()
    {
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            Exec(connection, $"CREATE TABLE future (x INTEGER); PRAGMA user_version = {ReportDatabase.SchemaVersion + 1};");
        }

        var before = File.ReadAllBytes(DbPath);
        var logger = new ListLogger<ReportDatabase>();
        var db = new ReportDatabase(DbPath, logger);

        db.Initialize();

        Assert.False(db.IsAvailable);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.Contains(logger.Errors, e => e.Contains("newer", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<StorageUnavailableException>(() => db.OpenConnection());
    }

    [Fact]
    public void Snapshot_HasSchemaVersion_NoAddressOrPath_AndFilterColumns()
    {
        var store = Store(Open());
        var playback = new PlaybackEntry
        {
            SessionId = "s1",
            State = PlaybackState.Stopped,
            UserId = Alice,
            Client = "Jellyfin Web",
            ClientVersion = "12.0.0",
            DeviceName = "Laptop",
            DeviceId = "device-1",
            ItemId = Movie,
            PlayMethod = "Transcode",
            Transcoding = new TranscodingDetails { VideoCodec = "h264", TranscodeReasons = ["ContainerBitrateExceedsLimit"] },
        };

        var filed = store.AddReport(NewReport(Alice, "stutter") with { Snapshot = ReportSnapshot.FromPlayback(playback) });

        var report = store.GetReport(filed.ReportId)!;
        var json = JsonNode.Parse(report.SnapshotJson!)!.AsObject();
        Assert.Equal(ReportSnapshot.CurrentSchemaVersion, json["schemaVersion"]!.GetValue<int>());
        var text = report.SnapshotJson!;
        Assert.DoesNotContain("RemoteEndPoint", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/media/", text, StringComparison.Ordinal);
        Assert.Equal("Transcode", report.PlayMethod);
        Assert.Equal("ContainerBitrateExceedsLimit", report.TranscodeReasons);
        Assert.Equal("Jellyfin Web", report.Client);
        Assert.Equal("Laptop", report.Device);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM report WHERE play_method = 'Transcode' AND client = 'Jellyfin Web' AND device = 'Laptop'"));
    }

    [Fact]
    public void Reports_JoinTheOpenIssue_AndStartANewOneAfterItCloses()
    {
        var store = Store(Open());
        var first = store.AddReport(NewReport(Alice, "stutter"));
        _clock.Advance(TimeSpan.FromDays(60));
        var second = store.AddReport(NewReport(Bob, "stutter"));
        var other = store.AddReport(NewReport(Bob, "audio"));

        Assert.Equal(first.IssueId, second.IssueId);
        Assert.NotEqual(first.IssueId, other.IssueId);
        Assert.Equal(IssueStatus.New, store.GetIssue(first.IssueId)!.Status);

        store.SetStatus(first.IssueId, IssueStatus.Fixed, actorUserId: null);
        var third = store.AddReport(NewReport(Alice, "stutter"));

        Assert.NotEqual(first.IssueId, third.IssueId);
        Assert.Equal(IssueStatus.Fixed, store.GetIssue(first.IssueId)!.Status);
        Assert.Equal(IssueStatus.New, store.GetIssue(third.IssueId)!.Status);
    }

    [Fact]
    public async Task ConcurrentReports_ShareOneNewIssue()
    {
        var db = Open();
        var stores = Enumerable.Range(0, 8).Select(_ => Store(db)).ToArray();

        var filed = await Task.WhenAll(stores.Select((s, i) => Task.Run(() => s.AddReport(NewReport(Guid.NewGuid(), "stutter")))));

        Assert.Single(filed.Select(f => f.IssueId).Distinct());
        Assert.Equal(1, Count("SELECT COUNT(*) FROM issue"));
    }

    [Fact]
    public void NewIssue_ChecksTheFileOfItsFirstReport()
    {
        var store = Store(Open());
        var modified = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        var filed = store.AddReport(NewReport(Alice, "stutter") with
        {
            ItemType = "Episode",
            SeriesId = Guid.NewGuid(),
            SeriesProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189" },
            SeasonNumber = 2,
            EpisodeNumber = 5,
            FileSize = 1_234_567,
            FileModified = modified,
        });

        var report = store.GetReport(filed.ReportId)!;
        Assert.Equal("81189", report.SeriesProviderIds["Tvdb"]);
        Assert.Equal(1_234_567, report.FileSize);
        Assert.Equal(modified, report.FileModified);
        var issue = store.GetIssue(filed.IssueId)!;
        Assert.Equal(1_234_567, issue.CheckedFileSize);
        Assert.Equal(modified, issue.CheckedFileModified);
    }

    [Fact]
    public void Purge_RemovesOldClosedIssues_KeepsOpenOnes_AndHonoursTheSetting()
    {
        var store = Store(Open());
        var old = store.AddReport(NewReport(Alice, "stutter", OtherMovie));
        var recent = store.AddReport(NewReport(Alice, "audio"));
        var ancient = store.AddReport(NewReport(Bob, "stutter"));
        store.SetStatus(old.IssueId, IssueStatus.Fixed, null);
        _clock.Advance(TimeSpan.FromDays(2));
        store.SetStatus(recent.IssueId, IssueStatus.WontFix, null);
        _clock.Advance(TimeSpan.FromDays(364));

        // `old` closed 366 days ago, `recent` 364 days ago; `ancient` was never closed.
        _retentionDays = 0;
        Assert.Equal(0, store.Purge());
        _retentionDays = 365;
        Assert.Equal(1, store.Purge());

        Assert.Null(store.GetIssue(old.IssueId));
        Assert.Null(store.GetReport(old.ReportId));
        Assert.Equal(0, Count($"SELECT COUNT(*) FROM event WHERE issue_id = {old.IssueId}"));
        Assert.NotNull(store.GetIssue(recent.IssueId));
        Assert.NotNull(store.GetIssue(ancient.IssueId));

        _retentionDays = 30;
        Assert.Equal(1, store.Purge());
        Assert.Null(store.GetIssue(recent.IssueId));
        Assert.NotNull(store.GetIssue(ancient.IssueId));
        Assert.True(Count("SELECT COUNT(*) FROM event WHERE kind = 'purged'") > 0);
    }

    [Fact]
    public void Reopening_StopsTheClock_AndIsRefusedWhileANewerIssueIsOpen()
    {
        var store = Store(Open());
        var first = store.AddReport(NewReport(Alice, "stutter"));
        store.SetStatus(first.IssueId, IssueStatus.Fixed, null);
        Assert.NotNull(store.GetIssue(first.IssueId)!.ClosedAt);

        Assert.Equal(StatusChange.Changed, store.SetStatus(first.IssueId, IssueStatus.Investigating, null).Result);
        Assert.Null(store.GetIssue(first.IssueId)!.ClosedAt);
        _clock.Advance(TimeSpan.FromDays(800));
        Assert.Equal(0, store.Purge());

        store.SetStatus(first.IssueId, IssueStatus.Fixed, null);
        var newer = store.AddReport(NewReport(Bob, "stutter"));
        var refused = store.SetStatus(first.IssueId, IssueStatus.New, null);

        Assert.Equal(StatusChange.NewerOpenIssue, refused.Result);
        Assert.Equal(newer.IssueId, refused.ConflictingIssueId);
        Assert.Equal(IssueStatus.Fixed, store.GetIssue(first.IssueId)!.Status);
    }

    [Fact]
    public void StatusChanges_AreRecordedAsEvents()
    {
        var store = Store(Open());
        var filed = store.AddReport(NewReport(Alice, "stutter"));
        var admin = Guid.NewGuid();

        store.SetStatus(filed.IssueId, IssueStatus.Investigating, admin);
        store.SetStatus(filed.IssueId, IssueStatus.Fixed, admin);

        var events = store.GetIssueEvents(filed.IssueId);
        Assert.Equal(["filed", "status changed", "status changed"], events.Select(e => e.Kind));
        Assert.Equal("New → Investigating", events[1].Detail);
        Assert.Equal(admin, events[2].ActorUserId);
    }

    [Fact]
    public void DeletedUser_IsAnonymisedEverywhere()
    {
        var store = Store(Open());
        var playback = new PlaybackEntry { SessionId = "s", UserId = Alice, DeviceName = "Alice's iPad", DeviceId = "alice-ipad", Client = "Jellyfin Web" };
        var a = store.AddReport(NewReport(Alice, "stutter") with { ReporterName = "alice", Device = "Alice's iPad", Snapshot = ReportSnapshot.FromPlayback(playback) });
        var b = store.AddReport(NewReport(Alice, "audio") with { ReporterName = "alice", Device = "Alice's iPad", Snapshot = ReportSnapshot.FromPlayback(playback) });
        store.AddReport(NewReport(Bob, "audio") with { ReporterName = "bob" });

        Assert.Equal(2, store.AnonymiseUser(Alice));

        foreach (var id in new[] { a.ReportId, b.ReportId })
        {
            var report = store.GetReport(id)!;
            Assert.True(report.IsDeletedUser);
            Assert.Null(report.ReporterUserId);
            Assert.Null(report.ReporterName);
            Assert.Null(report.Device);
        }

        var everything = Dump();
        Assert.DoesNotContain(Alice.ToString("N"), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Alice.ToString(), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", everything, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bob", everything, StringComparison.Ordinal);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM event WHERE kind = 'anonymised'"));
    }

    [Fact]
    public void Withdrawal_OnlyWhileNew_AndAnEmptyIssueGoes()
    {
        var store = Store(Open());
        var fresh = store.AddReport(NewReport(Alice, "stutter"));
        var worked = store.AddReport(NewReport(Alice, "audio"));
        store.SetStatus(worked.IssueId, IssueStatus.Investigating, null);

        Assert.Equal(Withdrawal.NotYours, store.Withdraw(fresh.ReportId, Bob));
        Assert.Equal(Withdrawal.Withdrawn, store.Withdraw(fresh.ReportId, Alice));
        Assert.Equal(Withdrawal.NotNew, store.Withdraw(worked.ReportId, Alice));

        Assert.Null(store.GetReport(fresh.ReportId));
        Assert.Null(store.GetIssue(fresh.IssueId));
        Assert.NotNull(store.GetReport(worked.ReportId));
    }

    [Fact]
    public void Withdrawal_KeepsAnIssueWithOtherReports()
    {
        var store = Store(Open());
        var mine = store.AddReport(NewReport(Alice, "stutter"));
        store.AddReport(NewReport(Bob, "stutter"));

        Assert.Equal(Withdrawal.Withdrawn, store.Withdraw(mine.ReportId, Alice));

        Assert.NotNull(store.GetIssue(mine.IssueId));
        Assert.Contains(store.GetIssueEvents(mine.IssueId), e => e.Kind == "withdrawn");
    }

    [Fact]
    public void MovingAnIssue_ToATitleWithAnOpenIssue_JoinsIt()
    {
        var store = Store(Open());
        var moved = store.AddReport(NewReport(Alice, "audio", Movie));
        var existing = store.AddReport(NewReport(Bob, "audio", OtherMovie));
        var closed = store.AddReport(NewReport(Bob, "stutter", Movie));
        store.SetStatus(closed.IssueId, IssueStatus.Fixed, null);

        store.MoveItemIssues(Movie, OtherMovie);

        Assert.Null(store.GetIssue(moved.IssueId));
        var joined = store.GetIssue(existing.IssueId)!;
        Assert.Equal(IssueStatus.ReplacedVerify, joined.Status);
        Assert.Equal(2, store.GetIssueReports(existing.IssueId).Count);
        Assert.Contains(store.GetIssueEvents(existing.IssueId), e => e.Kind == "relinked");
        Assert.Contains(store.GetIssueEvents(existing.IssueId), e => e.Kind == "filed" && e.ReportId == moved.ReportId);
        var movedClosed = store.GetIssue(closed.IssueId)!;
        Assert.Equal(OtherMovie, movedClosed.ItemId);
        Assert.Equal(IssueStatus.Fixed, movedClosed.Status);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM issue WHERE closed_at IS NULL"));
    }

    [Fact]
    public void AdminSeen_IsKeptPerAdmin()
    {
        var store = Store(Open());
        var admin = Guid.NewGuid();
        Assert.Null(store.GetAdminSeen(admin));

        store.MarkAdminSeen(admin);

        Assert.Equal(_clock.GetUtcNow(), store.GetAdminSeen(admin));
        Assert.Null(store.GetAdminSeen(Guid.NewGuid()));
    }

    [Fact]
    public async Task ConsistentCopy_WhileWriting_IsCompleteAndUsable()
    {
        var db = Open();
        var store = Store(db);
        for (var i = 0; i < 200; i++)
        {
            store.AddReport(NewReport(Guid.NewGuid(), "stutter", Guid.NewGuid()));
        }

        var writer = Task.Run(() =>
        {
            var other = Store(db);
            for (var i = 0; i < 200; i++)
            {
                other.AddReport(NewReport(Guid.NewGuid(), "audio", Guid.NewGuid()));
            }
        });
        store.WriteCopy();
        await writer;

        Assert.True(File.Exists(db.CopyPath));
        using var copy = new SqliteConnection($"Data Source={db.CopyPath};Mode=ReadOnly;Pooling=False");
        copy.Open();
        using var check = copy.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM report WHERE reason_code = 'stutter'";
        Assert.Equal(200L, check.ExecuteScalar());
        copy.Close();

        var restored = new ReportDatabase(db.CopyPath, NullLogger<ReportDatabase>.Instance);
        restored.Initialize();
        Assert.True(restored.IsAvailable);
        Assert.True(new ReportStore(restored, _clock, () => _retentionDays).GetReportCount() >= 200);
    }

    [Fact]
    public void Export_HoldsEverything_AndChangesNothing()
    {
        var store = Store(Open());
        var filed = store.AddReport(NewReport(Alice, "stutter") with { Snapshot = ReportSnapshot.FromPlayback(new PlaybackEntry { SessionId = "s", Client = "Jellyfin Web" }) });
        store.SetStatus(filed.IssueId, IssueStatus.Investigating, null);
        var before = File.ReadAllBytes(DbPath);

        using var output = new MemoryStream();
        store.Export(output);

        var doc = JsonNode.Parse(output.ToArray())!.AsObject();
        Assert.Equal(ReportDatabase.SchemaVersion, doc["schemaVersion"]!.GetValue<int>());
        Assert.Single(doc["reports"]!.AsArray());
        Assert.Single(doc["issues"]!.AsArray());
        Assert.Equal(2, doc["events"]!.AsArray().Count);
        Assert.Equal("Jellyfin Web", doc["reports"]![0]!["snapshot"]!["client"]!.GetValue<string>());
        Assert.Equal(before, File.ReadAllBytes(DbPath));
    }

    [Fact]
    public void RetentionSetting_DefaultsTo365_AndNegativeMeansOff()
    {
        var config = new PluginConfiguration();
        Assert.Equal(365, config.ReportRetentionDays);
        Assert.Equal(24, config.PlaybackRetentionHours);

        var store = Store(Open());
        var filed = store.AddReport(NewReport(Alice, "stutter"));
        store.SetStatus(filed.IssueId, IssueStatus.Fixed, null);
        _clock.Advance(TimeSpan.FromDays(1000));
        _retentionDays = -5;

        Assert.Equal(0, store.Purge());
        Assert.NotNull(store.GetIssue(filed.IssueId));
    }

    private ReportDatabase Open()
    {
        var db = new ReportDatabase(DbPath, NullLogger<ReportDatabase>.Instance);
        db.Initialize();
        return db;
    }

    private ReportStore Store(ReportDatabase db) => new(db, _clock, () => _retentionDays);

    private static NewReport NewReport(Guid user, string reason, Guid? item = null) => new()
    {
        ReporterUserId = user,
        ReporterName = "user-" + user.ToString("N")[..6],
        ItemId = item ?? Movie,
        ItemType = "Movie",
        ItemName = "Test Movie",
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "693134" },
        FilePath = "/media/movies/Test Movie (2026)/Test Movie (2026).mkv",
        ReasonCode = reason,
    };

    private long UserVersion() => Count("PRAGMA user_version");

    private long Count(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private List<string> Query(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private string Dump()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var text = new System.Text.StringBuilder();
        foreach (var table in new[] { "report", "issue", "event" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    text.Append(reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                }

                text.AppendLine();
            }
        }

        return text.ToString();
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
