using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Playback;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class ReportServiceTests : IDisposable
{
    private static readonly Guid MovieId = Guid.NewGuid();
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid EpisodeId = Guid.NewGuid();
    private static readonly Guid HiddenId = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly List<SessionInfo> _live = [];
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly PluginConfiguration _config = new();
    private readonly User _alice = new("alice", "auth", "reset");
    private readonly User _bob = new("bob", "auth", "reset");
    private readonly PlaybackCapture _capture;
    private readonly ReportStore _store;
    private readonly ReportService _service;
    private readonly string _moviePath;

    public ReportServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _moviePath = Path.Combine(_dir, "Test Movie (2026).mkv");
        File.WriteAllBytes(_moviePath, new byte[1234]);

        _sessions.SetupGet(m => m.Sessions).Returns(() => _live);
        _capture = new PlaybackCapture(_sessions.Object, _library.Object, _clock, () => TimeSpan.FromHours(24));
        _capture.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        var db = new ReportDatabase(Path.Combine(_dir, "media-reports.db"), NullLogger<ReportDatabase>.Instance);
        db.Initialize();
        _store = new ReportStore(db, _clock, () => 365);

        var movie = new Movie { Id = MovieId, Name = "Test Movie", Path = _moviePath, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "693134" } };
        var series = new Series { Id = SeriesId, Name = "Test Series", Path = _dir, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189" } };
        var episode = new Episode { Id = EpisodeId, Name = "Pilot", SeriesId = SeriesId, SeriesName = "Test Series", ParentIndexNumber = 2, IndexNumber = 5 };
        foreach (var user in new[] { _alice, _bob })
        {
            _users.Setup(m => m.GetUserById(user.Id)).Returns(user);
            _library.Setup(m => m.GetItemById<BaseItem>(MovieId, user)).Returns(movie);
            _library.Setup(m => m.GetItemById<BaseItem>(SeriesId, user)).Returns(series);
            _library.Setup(m => m.GetItemById<BaseItem>(EpisodeId, user)).Returns(episode);
        }

        _library.Setup(m => m.GetItemById(SeriesId)).Returns(series);
        _service = new ReportService(_library.Object, _users.Object, _capture, _store, () => _config, _clock);
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
    public void Context_NamesTheTitle_ActiveReasonsInOrder_AndNoPlaybackWhenNoneWasSeen()
    {
        _config.Reasons = PluginConfiguration.DefaultReasons().Select(r => r.Code == "metadata" ? r with { Retired = true } : r).ToArray();

        var context = Ok(_service.GetContext(_alice.Id, "phone-id", MovieId));

        Assert.Equal("Test Movie", context.Name);
        Assert.Equal("Movie", context.Type);
        Assert.Equal(["wont-play", "playback-error", "poor-quality", "stutter", "audio", "subtitles", "other"], context.Reasons.Select(r => r.Code));
        Assert.Null(context.Playback);
        Assert.Empty(context.OpenReports);
    }

    [Fact]
    public void Context_PrefersLivePlaybackOnTheCallingDevice()
    {
        Play("tv", _alice, "Living Room TV");
        Play("phone", _alice, "Phone");

        var context = Ok(_service.GetContext(_alice.Id, "Phone-id", MovieId));

        Assert.Equal("Phone", context.Playback!.DeviceName);
        Assert.Equal("Playing", context.Playback.State);
    }

    [Fact]
    public void Context_FromAPhone_NamesTheTvPlaybackThatStopped()
    {
        Play("tv", _alice, "Living Room TV");
        Stop("tv", _alice, "Living Room TV");
        _clock.Advance(TimeSpan.FromHours(1));

        var context = Ok(_service.GetContext(_alice.Id, "Phone-id", MovieId));

        Assert.Equal("Living Room TV", context.Playback!.DeviceName);
        Assert.Equal("Stopped", context.Playback.State);
        Assert.Equal(_clock.GetUtcNow() - TimeSpan.FromHours(1), context.Playback.LastSeen);
    }

    [Fact]
    public void Context_ForASeries_NamesNoPlayback()
    {
        Play("tv", _alice, "Living Room TV");

        var context = Ok(_service.GetContext(_alice.Id, "tv-id", SeriesId));

        Assert.Equal("Series", context.Type);
        Assert.Null(context.Playback);
    }

    [Fact]
    public void Context_ListsTheCallersOwnOpenReports()
    {
        Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        Ok(_service.File(_bob.Id, null, new FileReportRequest(MovieId, "audio", null)));

        var context = Ok(_service.GetContext(_alice.Id, null, MovieId));

        var open = Assert.Single(context.OpenReports);
        Assert.Equal("stutter", open.ReasonCode);
        Assert.Equal("Stutter or jitter", open.ReasonLabel);
        Assert.Equal("New", open.Status);
    }

    [Fact]
    public void File_StoresThePlaybackTheServerChose_AndGroupsWithOtherUsers()
    {
        Play("tv", _alice, "Living Room TV", transcoding: true);
        Stop("tv", _alice, "Living Room TV");

        var first = Ok(_service.File(_alice.Id, "Phone-id", new FileReportRequest(MovieId, "stutter", "  keeps jumping back  ")));
        var second = Ok(_service.File(_bob.Id, null, new FileReportRequest(MovieId, "stutter", null)));

        Assert.Equal(first.IssueId, second.IssueId);
        Assert.Equal("New", first.Status);
        var stored = _store.GetReport(first.Id)!;
        Assert.Equal("keeps jumping back", stored.Note);
        Assert.Equal("alice", stored.ReporterName);
        Assert.Equal("Living Room TV", stored.Device);
        Assert.Equal("Transcode", stored.PlayMethod);
        Assert.Contains("\"state\":\"Stopped\"", stored.SnapshotJson!, StringComparison.Ordinal);
        Assert.Equal(_moviePath, stored.FilePath);
        Assert.Equal(1234, stored.FileSize);
        Assert.Equal("693134", stored.ProviderIds["Tmdb"]);
        Assert.Null(_store.GetReport(second.Id)!.SnapshotJson);
    }

    [Fact]
    public void File_FromThePlayer_CarriesTheLivePlayback()
    {
        Play("browser", _alice, "Edge");

        var filed = Ok(_service.File(_alice.Id, "Edge-id", new FileReportRequest(MovieId, "poor-quality", null)));

        Assert.Contains("\"state\":\"Playing\"", _store.GetReport(filed.Id)!.SnapshotJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void File_ForAnEpisode_NamesTheEpisodeWithItsSeries()
    {
        var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(EpisodeId, "subtitles", null)));

        var stored = _store.GetReport(filed.Id)!;
        Assert.Equal(EpisodeId, stored.ItemId);
        Assert.Equal("Episode", stored.ItemType);
        Assert.Equal(SeriesId, stored.SeriesId);
        Assert.Equal("Test Series", stored.SeriesName);
        Assert.Equal("81189", stored.SeriesProviderIds["Tvdb"]);
        Assert.Equal(2, stored.SeasonNumber);
        Assert.Equal(5, stored.EpisodeNumber);
    }

    [Fact]
    public void File_ForASeries_HasNoSnapshot()
    {
        Play("tv", _alice, "Living Room TV");

        var filed = Ok(_service.File(_alice.Id, "tv-id", new FileReportRequest(SeriesId, "wont-play", null)));

        Assert.Null(_store.GetReport(filed.Id)!.SnapshotJson);
    }

    [Fact]
    public void File_RefusesEachBadReport_AndStoresNothing()
    {
        _config.Reasons = PluginConfiguration.DefaultReasons().Select(r => r.Code == "metadata" ? r with { Retired = true } : r).ToArray();
        Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));

        AssertRefused(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "nonsense", null)), Outcome.BadRequest, "reason-not-allowed");
        AssertRefused(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "metadata", null)), Outcome.BadRequest, "reason-not-allowed");
        AssertRefused(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "audio", new string('x', 1001))), Outcome.BadRequest, "note-too-long");
        AssertRefused(_service.File(_alice.Id, null, new FileReportRequest(HiddenId, "audio", null)), Outcome.NotFound, "item-not-found");
        AssertRefused(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)), Outcome.Conflict, "already-reported");

        Assert.Equal(1, _store.GetReportCount());
        Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "audio", new string('x', 1000))));
    }

    [Fact]
    public void ReportingOff_RefusesContextAndFiling_AndOnAgainWorksWithoutRestart()
    {
        _config.ReportingEnabled = false;

        AssertRefused(_service.GetContext(_alice.Id, null, MovieId), Outcome.Forbidden, "reporting-off");
        AssertRefused(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)), Outcome.Forbidden, "reporting-off");
        Assert.Equal(0, _store.GetReportCount());

        _config.ReportingEnabled = true;
        Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
    }

    [Fact]
    public void Responses_HoldNoPathAddressOrOtherUsers()
    {
        Play("tv", _alice, "Living Room TV", transcoding: true);
        Ok(_service.File(_bob.Id, null, new FileReportRequest(MovieId, "audio", null)));

        var context = JsonSerializer.Serialize(Ok(_service.GetContext(_alice.Id, "tv-id", MovieId)));
        var filed = JsonSerializer.Serialize(Ok(_service.File(_alice.Id, "tv-id", new FileReportRequest(MovieId, "stutter", null))));

        foreach (var json in new[] { context, filed })
        {
            Assert.DoesNotContain(_dir, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".mkv", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bob", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RemoteEndPoint", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static T Ok<T>(ServiceResult<T> result)
    {
        Assert.True(result.Outcome == Outcome.Ok, $"{result.Outcome} {result.Code}");
        return result.Value!;
    }

    private static void AssertRefused<T>(ServiceResult<T> result, Outcome outcome, string code)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.Code);
    }

    private void Play(string id, User user, string device, bool transcoding = false)
    {
        var session = Session(id, user, device);
        session.NowPlayingItem = new BaseItemDto { Id = MovieId, Name = "Test Movie" };
        session.PlayState = new PlayerStateInfo { PlayMethod = transcoding ? PlayMethod.Transcode : PlayMethod.DirectPlay, PositionTicks = 100 };
        if (transcoding)
        {
            session.TranscodingInfo = new TranscodingInfo { VideoCodec = "h264", TranscodeReasons = TranscodeReason.ContainerBitrateExceedsLimit };
        }

        _live.RemoveAll(s => s.Id == id);
        _live.Add(session);
        _sessions.Raise(m => m.PlaybackProgress += null, new PlaybackProgressEventArgs { Session = session });
    }

    private void Stop(string id, User user, string device)
    {
        _live.RemoveAll(s => s.Id == id);
        _sessions.Raise(m => m.PlaybackStopped += null, new PlaybackStopEventArgs { Session = Session(id, user, device), PlaybackPositionTicks = 200 });
    }

    private static SessionInfo Session(string id, User user, string device) => new(Mock.Of<ISessionManager>(), NullLogger.Instance)
    {
        Id = id,
        UserId = user.Id,
        Client = "Test Client",
        ApplicationVersion = "1.0.0",
        DeviceName = device,
        DeviceId = device.Split(' ')[^1] + "-id",
    };
}
