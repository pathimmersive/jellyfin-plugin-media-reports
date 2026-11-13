using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Playback;
using Jellyfin.Plugin.MediaReports.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class SuspectWatcherTests : IDisposable
{
    private static readonly Guid Movie = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly PluginConfiguration _config = new();
    private readonly List<(string Kind, string? Client)> _added = [];
    private readonly ReportDatabase _db;
    private readonly ReportStore _store;
    private readonly SuspectWatcher _watcher;
    private DateTimeOffset _at;

    public SuspectWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new ReportDatabase(Path.Combine(_dir, "media-reports.db"), NullLogger<ReportDatabase>.Instance);
        _db.Initialize();
        _store = new ReportStore(_db, _clock, () => 365);
        _watcher = new SuspectWatcher(_store, () => _config, _ => new FindingTitle("Movie", "Test Movie", null, null, null, null), (k, c) => _added.Add((k, c)), NullLogger<SuspectWatcher>.Instance);
        _at = _clock.GetUtcNow();
    }

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
    public void RunA_SmoothDirectPlay_FindsNothing()
    {
        for (var s = 0; s < 300; s += 10)
        {
            Sample(s, s, "DirectPlay");
        }

        Assert.Empty(Findings());
    }

    [Fact]
    public void RunB_SlowTranscodeWithBackJumps_FindsFreezingAndTranscoderTooSlow()
    {
        var position = 0.0;
        for (var s = 0; s <= 240; s += 5)
        {
            position += 5;
            if (s >= 75 && s % 30 == 15)
            {
                position -= 12.5;
            }

            Sample(s, position, "Transcode", fps: 3.2f, completion: 20, sourceFps: 24);
        }

        var found = Findings();
        Assert.Equal(["freezing", "slow-transcode"], found.Keys.Order());
        Assert.Matches(@"^Position jumped back 8 s, \d+ times in \d+ minutes?$", found["freezing"]);
        Assert.Matches(@"^Transcoding at 3\.2 fps against 24 fps for \d+ s$", found["slow-transcode"]);
    }

    [Fact]
    public void RunC_BackJumpsUnderDirectPlay_FindsFreezingOnly()
    {
        Sample(0, 0, "DirectPlay");
        Sample(70, 58, "DirectPlay");
        Sample(80, 8, "DirectPlay");
        Sample(100, 26, "DirectPlay");
        Sample(110, 13, "DirectPlay");
        Sample(140, 43, "DirectPlay");
        Sample(150, 13, "DirectPlay");

        var found = Findings();
        Assert.Equal(["freezing"], found.Keys);
        Assert.Equal("Position jumped back 31 s, 3 times in 2 minutes", found["freezing"]);
        Assert.Equal([("freezing", "Jellyfin Roku")], _added);
    }

    [Fact]
    public void RunD_BitrateCap_FindsLowQuality()
    {
        Sample(0, 0, "Transcode", fps: 321, completion: 30, sourceFps: 24, bitrate: 1_060_000, height: 720, sourceBitrate: 20_000_000, sourceHeight: 1080, reasons: ["ContainerBitrateExceedsLimit"]);

        Assert.Equal("Sent at 1.1 Mbps 720p from 20 Mbps 1080p (bitrate limit)", Findings()["low-quality"]);
        Assert.Single(Findings());
    }

    [Fact]
    public void RunE_SwitchToANewStreamAfterADirectPlayError_FindsPlaybackErrorMidPlay()
    {
        Sample(0, 0, "DirectPlay");
        Sample(118, 118, "DirectPlay");
        Sample(122, 123, "Transcode", fps: 200, completion: 10, sourceFps: 24, reasons: ["DirectPlayError"], playSession: "after-error");

        Assert.Equal("Switched to transcoding: direct play error", Findings()["playback-error"]);
    }

    [Fact]
    public void ATranscodeReportedAsDirectPlayUntilItsJobStarts_IsNoPlaybackError()
    {
        Sample(0, 0, "DirectPlay");
        Sample(5, 5, "Transcode", fps: 1, completion: 1, sourceFps: 24, reasons: ["VideoCodecNotSupported", "DirectPlayError"]);

        Assert.DoesNotContain("playback-error", Findings().Keys);
    }

    [Fact]
    public void TwoRewinds_MakeNoFinding_AndTheFirstMinuteIsIgnored()
    {
        Sample(0, 0, "DirectPlay");
        Sample(20, 20, "DirectPlay");
        Sample(30, 5, "DirectPlay");
        Sample(100, 75, "DirectPlay");
        Sample(110, 70, "DirectPlay");
        Sample(300, 260, "DirectPlay");
        Sample(310, 250, "DirectPlay");

        Assert.Empty(Findings());
    }

    [Fact]
    public void JumpsWhilePaused_OrSpreadOverMoreThanTenMinutes_AreNotFreezing()
    {
        Sample(0, 0, "DirectPlay");
        Sample(70, 70, "DirectPlay");
        Sample(80, 60, "DirectPlay", paused: true);
        Sample(90, 50, "DirectPlay", paused: true);
        Sample(100, 40, "DirectPlay", paused: true);
        Sample(110, 50, "DirectPlay");
        Sample(120, 40, "DirectPlay");
        Sample(500, 400, "DirectPlay");
        Sample(510, 390, "DirectPlay");
        Sample(900, 800, "DirectPlay");
        Sample(910, 790, "DirectPlay");

        Assert.Empty(Findings());
    }

    [Fact]
    public void SlowTranscode_NeedsThirtySeconds_AndAFastSampleResetsIt()
    {
        Sample(0, 0, "Transcode", fps: 10, completion: 5, sourceFps: 24);
        Sample(20, 20, "Transcode", fps: 10, completion: 6, sourceFps: 24);
        Sample(25, 25, "Transcode", fps: 30, completion: 7, sourceFps: 24);
        Sample(50, 50, "Transcode", fps: 10, completion: 8, sourceFps: 24);
        Sample(60, 60, "Transcode", fps: 22, completion: 100, sourceFps: 24);
        Assert.Empty(Findings());

        Sample(81, 81, "Transcode", fps: 10, completion: 9, sourceFps: 24);
        Assert.Contains("slow-transcode", Findings().Keys);
    }

    [Fact]
    public void FastTranscode_IsNotSlow()
    {
        for (var s = 0; s <= 120; s += 10)
        {
            Sample(s, s, "Transcode", fps: 24 * 13, completion: s / 2.0, sourceFps: 24);
        }

        Assert.Empty(Findings());
    }

    [Fact]
    public void ABitrateLimitThatKeepsTheSourceQuality_IsNotLowQuality()
    {
        Sample(0, 0, "Transcode", bitrate: 8_000_000, height: 1080, sourceBitrate: 20_000_000, sourceHeight: 1080, reasons: ["ContainerBitrateExceedsLimit"]);
        Sample(10, 10, "Transcode", bitrate: 1_000_000, height: 480, sourceBitrate: 20_000_000, sourceHeight: 1080, reasons: ["VideoCodecNotSupported"]);

        Assert.Empty(Findings());
    }

    [Fact]
    public void AKindSwitchedOff_AddsNoFinding_TheOthersStillDo()
    {
        _config.SuspectFreezing = false;

        RunC_Samples();
        Sample(200, 160, "Transcode", bitrate: 1_000_000, height: 720, sourceBitrate: 20_000_000, sourceHeight: 1080, reasons: ["ContainerBitrateExceedsLimit"]);

        Assert.Equal(["low-quality"], Findings().Keys);
    }

    [Fact]
    public void OneFindingPerPlaybackAndKind_LaterJumpsUpdateIt()
    {
        RunC_Samples();
        Sample(160, 20, "DirectPlay");
        Sample(170, 5, "DirectPlay");

        Assert.Equal(1, Count("SELECT COUNT(*) FROM finding WHERE kind = 'freezing'"));
        Assert.Single(_added);
        Assert.Contains(", 4 times in", Findings()["freezing"], StringComparison.Ordinal);
    }

    [Fact]
    public void AFinding_HoldsNoPathOrAddress_AndMakesNoReportOrIssue()
    {
        RunC_Samples();

        var row = Row("SELECT * FROM finding");
        Assert.DoesNotContain(row.Values, v => v is string s && (s.Contains(".mkv", StringComparison.Ordinal) || s.Contains("192.168.", StringComparison.Ordinal)));
        Assert.Equal("Test Movie", row["title"]);
        Assert.Equal("alice", row["user_name"]);
        Assert.Equal("Living Room TV", row["device"]);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM report") + Count("SELECT COUNT(*) FROM issue") + Count("SELECT COUNT(*) FROM event"));
    }

    [Fact]
    public void Findings_AreKeptThirtyDaysAfterTheyWereLastSeen()
    {
        RunC_Samples();
        _clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
        Assert.Equal(0, _store.PurgeFindings());

        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, _store.PurgeFindings());
        Assert.Equal(0, Count("SELECT COUNT(*) FROM finding"));
    }

    [Fact]
    public void Metrics_CountFindingsByKindAndClient_AndRecentOnesByKind()
    {
        var metrics = new ReportMetrics(_store, () => _config, "0.1.0.0", _clock);
        var watcher = new SuspectWatcher(_store, () => _config, _ => new FindingTitle("Movie", "Test Movie", null, null, null, null), metrics.SuspectAdded, NullLogger<SuspectWatcher>.Instance);
        foreach (var (s, p) in RunC)
        {
            watcher.Observe(At(s, p, "DirectPlay"));
        }

        var text = metrics.Render();

        Assert.Contains("media_reports_suspected_problems_total{kind=\"freezing\",client=\"Jellyfin Roku\"} 1\n", text, StringComparison.Ordinal);
        Assert.Contains("media_reports_suspected_problems_recent{kind=\"freezing\"} 1\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueuedSamples_RunOffTheCallersThread_AndAFailureIsLogged()
    {
        var logger = new ListLogger<SuspectWatcher>();
        var watcher = new SuspectWatcher(_store, () => _config, _ => throw new InvalidOperationException("library gone"), (_, _) => { }, logger);

        foreach (var (s, p) in RunC)
        {
            watcher.Queue(At(s, p, "DirectPlay"));
        }

        await watcher.WhenIdle;
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public void Mbps_ReadsLikeTheSpec()
    {
        Assert.Equal("1.1 Mbps", SuspectWatcher.Mbps(1_060_000));
        Assert.Equal("20 Mbps", SuspectWatcher.Mbps(20_000_000));
    }

    private static readonly (int Seconds, double Position)[] RunC = [(0, 0), (70, 58), (80, 8), (100, 26), (110, 13), (140, 43), (150, 13)];

    private void RunC_Samples()
    {
        foreach (var (s, p) in RunC)
        {
            Sample(s, p, "DirectPlay");
        }
    }

    private void Sample(int seconds, double position, string method, float? fps = null, double? completion = null, float? sourceFps = null,
        int? bitrate = null, int? height = null, int? sourceBitrate = null, int? sourceHeight = null, string[]? reasons = null, bool paused = false,
        string playSession = "play-1") =>
        _watcher.Observe(At(seconds, position, method, fps, completion, sourceFps, bitrate, height, sourceBitrate, sourceHeight, reasons, paused) with { PlaySessionId = playSession });

    private SuspectSample At(int seconds, double position, string method, float? fps = null, double? completion = null, float? sourceFps = null,
        int? bitrate = null, int? height = null, int? sourceBitrate = null, int? sourceHeight = null, string[]? reasons = null, bool paused = false) => new()
    {
        At = _at + TimeSpan.FromSeconds(seconds),
        SessionId = "session-1",
        UserId = User,
        UserName = "alice",
        Client = "Jellyfin Roku",
        DeviceName = "Living Room TV",
        ItemId = Movie,
        PositionTicks = TimeSpan.FromSeconds(position).Ticks,
        IsPaused = paused,
        PlayMethod = method,
        TranscodeFps = fps,
        Completion = completion,
        OutputBitrate = bitrate,
        OutputHeight = height,
        TranscodeReasons = reasons ?? [],
        SourceFps = sourceFps,
        SourceBitrate = sourceBitrate,
        SourceHeight = sourceHeight,
    };

    private Dictionary<string, string> Findings()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, detail FROM finding";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            found[reader.GetString(0)] = reader.GetString(1);
        }

        return found;
    }

    private Dictionary<string, object?> Row(string sql)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return Enumerable.Range(0, reader.FieldCount).ToDictionary(i => reader.GetName(i), i => reader.IsDBNull(i) ? null : reader.GetValue(i));
    }

    private int Count(string sql)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
