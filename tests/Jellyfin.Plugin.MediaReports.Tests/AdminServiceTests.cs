using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Notifications;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class AdminServiceTests : IDisposable
{
    private static readonly Guid MovieId = Guid.NewGuid();
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid SeasonId = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IMediaSourceManager> _mediaSources = new();
    private readonly Mock<IProviderManager> _providers = new();
    private readonly PluginConfiguration _config = new();
    private readonly User _admin = new("admin", "auth", "reset");
    private readonly User _otherAdmin = new("second-admin", "auth", "reset");
    private readonly User _alice = new("alice", "auth", "reset");
    private readonly User _bob = new("bob", "auth", "reset");
    private readonly ReportStore _store;
    private readonly AdminService _service;
    private readonly Movie _movie;

    public AdminServiceTests()
    {
        Directory.CreateDirectory(_dir);
        var db = new ReportDatabase(Path.Combine(_dir, "media-reports.db"), NullLogger<ReportDatabase>.Instance);
        db.Initialize();
        _store = new ReportStore(db, _clock, () => 365);

        foreach (var user in new[] { _admin, _otherAdmin, _alice, _bob })
        {
            _users.Setup(m => m.GetUserById(user.Id)).Returns(user);
        }

        _movie = new Movie { Id = MovieId, Name = "Test Movie", Path = "/media/movies/Test Movie (2026)/Test Movie (2026).mkv", Size = 4321, Container = "mkv", TotalBitrate = 2_000_000, ProductionYear = 2026 };
        _library.Setup(m => m.GetItemById(MovieId)).Returns(_movie);
        _mediaSources.Setup(m => m.GetMediaStreams(MovieId)).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Profile = "High", Width = 1280, Height = 720, RealFrameRate = 24 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = "aac", Language = "eng", IsDefault = true },
            new MediaStream { Type = MediaStreamType.Subtitle, Codec = "srt", Language = "fre" },
        ]);
        _service = new AdminService(_store, _library.Object, _users.Object, _mediaSources.Object, _providers.Object, Mock.Of<IFileSystem>(), () => _config, () => "server-1", new ReporterNotifier(Mock.Of<MediaBrowser.Controller.Session.ISessionManager>(), _store, NullLogger<ReporterNotifier>.Instance));
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
    public void Summary_CountsOpenIssuesAndNewOnes_PerAdmin_AndMarkSeenClearsOnlyTheCallers()
    {
        File(_alice, MovieId, "stutter");
        File(_bob, MovieId, "stutter");
        var episode = File(_alice, Guid.NewGuid(), "audio", type: "Episode");
        _store.SetStatus(episode.IssueId, IssueStatus.Fixed, _admin.Id);

        Assert.Equal(new AdminSummary(1, 2, 1), _service.GetSummary(_admin.Id, markSeen: true));
        Assert.Equal(new AdminSummary(1, 2, 0), _service.GetSummary(_admin.Id, markSeen: false));
        Assert.Equal(new AdminSummary(1, 2, 1), _service.GetSummary(_otherAdmin.Id, markSeen: false));

        _clock.Advance(TimeSpan.FromMinutes(5));
        File(_alice, Guid.NewGuid(), "other");
        Assert.Equal(new AdminSummary(2, 3, 1), _service.GetSummary(_admin.Id, markSeen: false));
    }

    [Fact]
    public void Issues_With120OpenIssues_GivesPagesOf50NewestFirst()
    {
        var ids = Enumerable.Range(0, 120).Select(_ => File(_alice, Guid.NewGuid(), "other").IssueId).ToList();

        var first = List(new IssueQuery());
        var third = List(new IssueQuery { Page = 3 });

        Assert.Equal(120, first.Total);
        Assert.Equal(50, first.Rows.Count);
        Assert.Equal(ids.AsEnumerable().Reverse().Take(50), first.Rows.Select(r => r.IssueId!.Value));
        Assert.Equal(20, third.Rows.Count);
        Assert.Equal(ids.Take(20).Reverse(), third.Rows.Select(r => r.IssueId!.Value));
    }

    [Fact]
    public void Issues_RollThreeEpisodesOfASeasonWithOneReasonIntoASeasonRow_ButNotTwo()
    {
        var three = Enumerable.Range(1, 3).Select(e => File(_alice, Guid.NewGuid(), "subtitles", type: "Episode", season: 1, episode: e)).ToList();
        var two = Enumerable.Range(1, 2).Select(e => File(_bob, Guid.NewGuid(), "subtitles", type: "Episode", season: 2, episode: e)).ToList();
        _store.SetStatus(three[0].IssueId, IssueStatus.Investigating, _admin.Id);

        var list = List(new IssueQuery());

        var season = Assert.Single(list.Rows, r => r.Kind == "season");
        Assert.Equal((3, 3, "Mixed", "Season 1"), (season.EpisodeCount!.Value, season.Children.Count, season.Status, season.Line));
        Assert.Equal(three.Select(f => f.IssueId), season.Children.Select(c => c.IssueId!.Value));
        Assert.Equal(3, list.Total);
        Assert.All(two, f => Assert.Contains(list.Rows, r => r.IssueId == f.IssueId));
    }

    [Fact]
    public void Issues_ByTitleAndClient_GiveOneRowPerClient_AndFiltersAndSearchNarrowThem()
    {
        var issue = File(_alice, MovieId, "stutter", client: "Jellyfin Web").IssueId;
        File(_bob, MovieId, "stutter", client: "Jellyfin Web");
        File(_admin, MovieId, "stutter", client: "Jellyfin for WebOS");
        File(_bob, Guid.NewGuid(), "audio", name: "Other Film", client: "Jellyfin Roku");

        var byClient = List(new IssueQuery { GroupBy = "titleClient", Reason = "stutter" });
        Assert.Equal([("Jellyfin Web", 2), ("Jellyfin for WebOS", 1)], byClient.Rows.OrderByDescending(r => r.Reports).Select(r => (r.Client!, r.Reports)));
        Assert.All(byClient.Rows, r => Assert.Equal((issue, "New"), (r.IssueId!.Value, r.Status)));

        Assert.Equal(issue, Assert.Single(List(new IssueQuery { Client = "Jellyfin for WebOS" }).Rows).IssueId);
        Assert.Equal("Other Film", Assert.Single(List(new IssueQuery { Q = "other" }).Rows).Title);
        var movie = Assert.Single(List(new IssueQuery { Q = "test movie" }).Rows);
        Assert.Equal((3, 3, "2026"), (movie.Reports, movie.People, movie.Line));
        Assert.Equal(["Jellyfin for WebOS", "Jellyfin Roku", "Jellyfin Web"], List(new IssueQuery()).Clients);
        Assert.Equal(Outcome.BadRequest, _service.ListIssues(_admin.Id, new IssueQuery { View = "everything" }).Outcome);
    }

    [Fact]
    public void Issues_Views_ShowOnlyTheirStatuses()
    {
        var open = File(_alice, Guid.NewGuid(), "audio").IssueId;
        var closed = File(_alice, Guid.NewGuid(), "audio").IssueId;
        _store.SetStatus(closed, IssueStatus.WontFix, _admin.Id);

        Assert.Equal([open], List(new IssueQuery()).Rows.Select(r => r.IssueId!.Value));
        Assert.Equal([closed], List(new IssueQuery { View = "closed" }).Rows.Select(r => r.IssueId!.Value));
        Assert.Equal(2, List(new IssueQuery { View = "all" }).Total);
    }

    [Fact]
    public void Issue_HasReportsNewestFirst_TheSnapshot_HistoryWithNames_AndTheFileAsJellyfinHasItNow()
    {
        var issue = File(_alice, MovieId, "stutter", client: "Jellyfin Web", note: "keeps jumping back").IssueId;
        _clock.Advance(TimeSpan.FromMinutes(1));
        File(_bob, MovieId, "stutter");
        _store.SetStatus(issue, IssueStatus.Investigating, _admin.Id);

        var detail = Value(_service.GetIssue(issue));

        Assert.Equal(["bob", "alice"], detail.Reports.Select(r => r.ReporterName));
        var alice = detail.Reports[1];
        Assert.Equal(("keeps jumping back", "Transcode", 600_000_000L), (alice.Note, alice.PlayMethod, alice.PositionTicks!.Value));
        Assert.Equal("Living Room TV", alice.Snapshot!.Value.GetProperty("deviceName").GetString());
        Assert.Null(detail.Reports[0].Snapshot);
        Assert.Equal([("filed", "alice"), ("filed", "bob"), ("status changed", "admin")], detail.History.Select(h => (h.Kind, h.ActorName!)));
        Assert.Equal((2, "server-1", "Investigating"), (detail.People, detail.ServerId, detail.Status));

        var file = detail.File;
        Assert.True(file.InLibrary);
        Assert.Equal((_movie.Path, 4321L, "mkv", 2_000_000), (file.Path, file.Size!.Value, file.Container, file.Bitrate!.Value));
        Assert.Equal(["Video", "Audio", "Subtitle"], file.Streams.Select(s => s.Type));
        Assert.Equal(("h264", 1280, 720, "SDR"), (file.Streams[0].Codec, file.Streams[0].Width!.Value, file.Streams[0].Height!.Value, file.Streams[0].VideoRange));
    }

    [Fact]
    public void Issue_OnASeries_ShowsItsFolderAndEpisodeCount_AndARemovedTitleShowsTheStoredPath()
    {
        var series = new Series { Id = SeriesId, Name = "Test Series", Path = "/media/shows/Test Series (2026)" };
        _library.Setup(m => m.GetItemById(SeriesId)).Returns(series);
        _library.Setup(m => m.GetCount(It.Is<InternalItemsQuery>(q => q.AncestorIds.Contains(SeriesId) && q.Recursive))).Returns(12);
        var onSeries = File(_alice, SeriesId, "metadata", type: "Series", name: "Test Series", path: series.Path).IssueId;
        var gone = File(_alice, Guid.NewGuid(), "audio", path: "/media/movies/Gone/Gone.mkv").IssueId;

        var seriesFile = Value(_service.GetIssue(onSeries)).File;
        Assert.Equal((true, series.Path, 12), (seriesFile.InLibrary, seriesFile.Path, seriesFile.EpisodeCount!.Value));
        Assert.Empty(seriesFile.Streams);

        var goneFile = Value(_service.GetIssue(gone)).File;
        Assert.Equal((false, "/media/movies/Gone/Gone.mkv"), (goneFile.InLibrary, goneFile.Path));
        Assert.Equal("issue-not-found", _service.GetIssue(999).Code);
    }

    [Fact]
    public void Status_Fixed_RecordsTheChangeAndClosedTime_AndOnlyStatusesAnAdminMaySetAreTaken()
    {
        var issue = File(_alice, MovieId, "stutter").IssueId;

        foreach (var status in new[] { "ReplacedVerify", "Bogus", "3", "fixed" })
        {
            Assert.Equal("status-not-allowed", _service.UpdateIssue(issue, new IssuePatch(status, "kept?"), _admin.Id).Code);
        }

        Assert.Null(_store.GetIssue(issue)!.AdminNotes);
        var fixedIssue = Value(_service.UpdateIssue(issue, new IssuePatch("Fixed", null), _admin.Id));
        Assert.Equal(("Fixed", _clock.GetUtcNow()), (fixedIssue.Status, fixedIssue.ClosedAt!.Value));
        Assert.Equal(("status changed", "New → Fixed", "admin"), (fixedIssue.History[^1].Kind, fixedIssue.History[^1].Detail, fixedIssue.History[^1].ActorName!));

        var reopened = Value(_service.UpdateIssue(issue, new IssuePatch("Investigating", null), _admin.Id));
        Assert.Equal(("Investigating", (DateTimeOffset?)null), (reopened.Status, reopened.ClosedAt));
    }

    [Fact]
    public void Status_ReopenIsRefusedWhileANewerIssueIsOpen_NamingIt_AndNothingChanges()
    {
        var old = File(_alice, MovieId, "audio").IssueId;
        _store.SetStatus(old, IssueStatus.Fixed, _admin.Id);
        var newer = File(_bob, MovieId, "audio").IssueId;

        var refused = _service.UpdateIssue(old, new IssuePatch("New", "try again"), _admin.Id);

        Assert.Equal((Outcome.Conflict, "newer-open-issue"), (refused.Outcome, refused.Code));
        Assert.Equal([newer], refused.IssueIds);
        var stored = _store.GetIssue(old)!;
        Assert.Equal((IssueStatus.Fixed, (string?)null), (stored.Status, stored.AdminNotes));
    }

    [Fact]
    public void Notes_UpTo10000Characters_AreSavedWithAHistoryLine_AndLongerOnesAreRefused()
    {
        var issue = File(_alice, MovieId, "audio").IssueId;

        Assert.Equal("notes-too-long", _service.UpdateIssue(issue, new IssuePatch(null, new string('x', 10001)), _admin.Id).Code);
        var saved = Value(_service.UpdateIssue(issue, new IssuePatch(null, new string('x', 10000)), _admin.Id));
        Assert.Equal(10000, saved.Notes!.Length);
        Assert.Equal(("note added", "admin"), (saved.History[^1].Kind, saved.History[^1].ActorName!));

        var again = Value(_service.UpdateIssue(issue, new IssuePatch(null, new string('x', 10000)), _admin.Id));
        Assert.Equal(saved.History.Count, again.History.Count);
    }

    [Fact]
    public void SetStatusForAll_ChangesEveryIssue_OrNoneWhenOneIsRefused()
    {
        var issues = Enumerable.Range(1, 3).Select(e => File(_alice, Guid.NewGuid(), "subtitles", type: "Episode", season: 1, episode: e).IssueId).ToList();
        var old = File(_alice, MovieId, "audio").IssueId;
        _store.SetStatus(old, IssueStatus.Fixed, _admin.Id);
        File(_bob, MovieId, "audio");

        var refused = _service.SetStatuses(new IssueStatusRequest([.. issues, old], "New"), _admin.Id);
        Assert.Equal("newer-open-issue", refused.Code);
        Assert.Equal([old], refused.IssueIds!);
        var missing = _service.SetStatuses(new IssueStatusRequest([issues[0], 999], "Investigating"), _admin.Id);
        Assert.Equal("issue-not-found", missing.Code);
        Assert.All(issues, i => Assert.Equal(IssueStatus.New, _store.GetIssue(i)!.Status));

        Assert.Equal(Outcome.Ok, _service.SetStatuses(new IssueStatusRequest(issues, "Investigating"), _admin.Id).Outcome);
        Assert.All(issues, i =>
        {
            Assert.Equal(IssueStatus.Investigating, _store.GetIssue(i)!.Status);
            Assert.Equal("status changed", _store.GetIssueEvents(i)[^1].Kind);
        });
        Assert.Equal("status-not-allowed", _service.SetStatuses(new IssueStatusRequest(issues, "ReplacedVerify"), _admin.Id).Code);
    }

    [Fact]
    public void Refresh_QueuesAFillingRefreshOfTheTitleOnly_AndWritesAHistoryLine()
    {
        var issue = File(_alice, MovieId, "metadata").IssueId;

        Assert.Equal(Outcome.Ok, _service.Refresh(issue, _admin.Id).Outcome);

        _providers.Verify(m => m.QueueRefresh(MovieId, It.Is<MetadataRefreshOptions>(o =>
            o.MetadataRefreshMode == MetadataRefreshMode.FullRefresh && o.ImageRefreshMode == MetadataRefreshMode.FullRefresh
            && !o.ReplaceAllMetadata && !o.ReplaceAllImages), RefreshPriority.High), Times.Once);
        _providers.VerifyNoOtherCalls();
        var last = _store.GetIssueEvents(issue)[^1];
        Assert.Equal(("action run", "Refresh metadata", _admin.Id), (last.Kind, last.Detail, last.ActorUserId!.Value));
    }

    [Fact]
    public void Scan_TargetsTheSeasonFolderOfAnEpisode_ElseItsSeries_AndAMovieItself_NeverTheLibrary()
    {
        var series = new Series { Id = SeriesId, Name = "Test Series", Path = "/media/shows/Test Series" };
        var season = new Season { Id = SeasonId, Name = "Season 1", Path = "/media/shows/Test Series/Season 01", SeriesId = SeriesId };
        var pathless = new Season { Id = Guid.NewGuid(), Name = "Season 2", SeriesId = SeriesId };
        var inFolder = new Episode { Id = Guid.NewGuid(), SeasonId = SeasonId, SeriesId = SeriesId };
        var loose = new Episode { Id = Guid.NewGuid(), SeasonId = pathless.Id, SeriesId = SeriesId };
        foreach (var item in new BaseItem[] { series, season, pathless, inFolder, loose })
        {
            _library.Setup(m => m.GetItemById(item.Id)).Returns(item);
        }

        Assert.Same(season, _service.ScanTarget(inFolder));
        Assert.Same(series, _service.ScanTarget(loose));
        Assert.Same(series, _service.ScanTarget(pathless));
        Assert.Same(season, _service.ScanTarget(season));
        Assert.Same(_movie, _service.ScanTarget(_movie));

        var issue = File(_alice, inFolder.Id, "subtitles", type: "Episode", season: 1, episode: 1).IssueId;
        Assert.Equal(Outcome.Ok, _service.Scan(issue, _admin.Id).Outcome);
        _providers.Verify(m => m.QueueRefresh(SeasonId, It.Is<MetadataRefreshOptions>(o => o.MetadataRefreshMode == MetadataRefreshMode.Default), RefreshPriority.High), Times.Once);
        _providers.VerifyNoOtherCalls();
        Assert.Equal(("action run", "Rescan: Season 1"), (_store.GetIssueEvents(issue)[^1].Kind, _store.GetIssueEvents(issue)[^1].Detail));
    }

    [Fact]
    public void QuickActions_OnARemovedTitle_AreRefusedAndRunNothing()
    {
        var issue = File(_alice, Guid.NewGuid(), "audio").IssueId;

        Assert.Equal("not-in-library", _service.Scan(issue, _admin.Id).Code);
        Assert.Equal("not-in-library", _service.Refresh(issue, _admin.Id).Code);
        Assert.Equal("issue-not-found", _service.Refresh(999, _admin.Id).Code);
        _providers.VerifyNoOtherCalls();
        Assert.Equal(["filed"], _store.GetIssueEvents(issue).Select(e => e.Kind));
    }

    [Fact]
    public async Task Controller_AnswersStorageUnavailable_WhenTheDatabaseDidNotOpen()
    {
        var db = new ReportDatabase(Path.Combine(_dir, "never-opened.db"), NullLogger<ReportDatabase>.Instance);
        var service = new AdminService(new ReportStore(db, _clock, () => 365), _library.Object, _users.Object, _mediaSources.Object, _providers.Object, Mock.Of<IFileSystem>(), () => _config, () => "server-1", new ReporterNotifier(Mock.Of<MediaBrowser.Controller.Session.ISessionManager>(), _store, NullLogger<ReporterNotifier>.Instance));
        var authorization = new Mock<IAuthorizationContext>();
        authorization.Setup(m => m.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(new AuthorizationInfo { User = _admin });
        var settings = new SettingsService(new ReportStore(db, _clock, () => 365), () => _config, _ => throw new InvalidOperationException("nothing is saved"));
        var alerts = new ReportAlerts(() => new System.Net.Http.HttpClient(), Mock.Of<MediaBrowser.Model.Activity.IActivityManager>(), () => _config, () => "http://localhost:8096", _clock, NullLogger<ReportAlerts>.Instance);
        var controller = new AdminController(service, settings, alerts, authorization.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var list = (await controller.ListIssues()).Result as ObjectResult;
        var one = controller.GetIssue(1).Result as ObjectResult;
        var read = controller.GetSettings().Result as ObjectResult;
        var save = controller.UpdateSettings(new SettingsUpdate { Reasons = [new ReasonUpdate(null, "New one", false)] }).Result as ObjectResult;

        foreach (var answer in new[] { list, one, read, save })
        {
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, answer!.StatusCode);
            Assert.Equal("storage-unavailable", ((IssueError)answer.Value!).Code);
        }
    }

    private FiledReport File(User user, Guid itemId, string reason, string type = "Movie", string? name = null, int? season = null, int? episode = null, string? client = null, string? note = null, string? path = null)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        return _store.AddReport(new NewReport
        {
            ReporterUserId = user.Id,
            ReporterName = user.Username,
            ItemId = itemId,
            ItemType = type,
            ItemName = name ?? (type == "Episode" ? $"Episode {episode}" : "Test Movie"),
            SeriesId = type is "Episode" or "Season" ? SeriesId : null,
            SeriesName = type is "Episode" or "Season" ? "Test Series" : null,
            SeasonNumber = season,
            EpisodeNumber = episode,
            FilePath = path ?? (itemId == MovieId ? _movie.Path : null),
            ReasonCode = reason,
            Note = note,
            Snapshot = client is null ? null : new ReportSnapshot { Client = client, DeviceName = "Living Room TV", PlayMethod = "Transcode", PositionTicks = 600_000_000 },
        });
    }

    [Fact]
    public void Findings_CountEachKindForSevenDays_NewestFirst_AndFilterByKind()
    {
        var movie = Guid.NewGuid();
        Finding("freezing", "s1", movie, "Living Room TV");
        _clock.Advance(TimeSpan.FromMinutes(5));
        Finding("low-quality", "s2", movie, "Living Room TV");
        Finding("freezing", "old", Guid.NewGuid(), "Den TV");
        _store.SaveFinding(new NewFinding { Kind = "slow-transcode", PlaybackKey = "gone", ItemId = Guid.NewGuid(), Title = "Old", Detail = "x" });
        _clock.Advance(TimeSpan.FromDays(8));
        Finding("freezing", "s3", movie, "Living Room TV");

        var list = Value(_service.ListFindings(null));

        Assert.Equal(["freezing", "slow-transcode", "low-quality", "playback-error"], list.Kinds.Select(k => k.Kind));
        Assert.Equal([1, 0, 0, 0], list.Kinds.Select(k => k.Count));
        Assert.Equal(["Freezing", "Transcoder too slow", "Low quality", "Playback error mid-play"], list.Kinds.Select(k => k.Label));
        Assert.Equal(5, list.Rows.Count);
        Assert.True(list.Rows.Select(r => r.LastSeen).SequenceEqual(list.Rows.Select(r => r.LastSeen).OrderByDescending(t => t)));
        Assert.All(Value(_service.ListFindings("freezing")).Rows, r => Assert.Equal("freezing", r.Kind));
        Assert.Equal(Outcome.BadRequest, _service.ListFindings("nonsense").Outcome);
        Assert.Equal(1, _service.GetSummary(_admin.Id, false).Suspected);
    }

    [Fact]
    public void Findings_OnOneTitleOnTwoDevices_NoteTheOtherDevice_AndAnEpisodeHasItsLine()
    {
        var episode = Guid.NewGuid();
        _store.SaveFinding(new NewFinding { Kind = "freezing", PlaybackKey = "a", ItemId = episode, Title = "Pilot", SeriesName = "Show", SeasonNumber = 1, EpisodeNumber = 2, Device = "Living Room TV", Client = "Jellyfin Roku", UserName = "alice", Detail = "Position jumped back 8 s, 3 times in 2 minutes" });
        _store.SaveFinding(new NewFinding { Kind = "low-quality", PlaybackKey = "b", ItemId = episode, Title = "Pilot", Device = "Bedroom TV", Detail = "x" });
        _store.SaveFinding(new NewFinding { Kind = "freezing", PlaybackKey = "c", ItemId = Guid.NewGuid(), Title = "Other", Device = "Living Room TV", Detail = "x" });

        var rows = Value(_service.ListFindings(null)).Rows;

        var a = rows.Single(r => r.Device == "Living Room TV" && r.Title == "Pilot");
        Assert.Equal(1, a.OtherDevices);
        Assert.Equal("Show · S1 E2", a.Line);
        Assert.Equal("Freezing", a.KindLabel);
        Assert.Equal(1, rows.Single(r => r.Device == "Bedroom TV").OtherDevices);
        Assert.Equal(0, rows.Single(r => r.Title == "Other").OtherDevices);
    }

    [Fact]
    public void Dismiss_HidesTheFindingAtOnce_AndAnUnknownOneIsNotFound()
    {
        Finding("freezing", "s1", Guid.NewGuid(), "Living Room TV");
        var id = Value(_service.ListFindings(null)).Rows.Single().Id;

        Assert.Equal(Outcome.Ok, _service.DismissFinding(id).Outcome);

        Assert.Empty(Value(_service.ListFindings(null)).Rows);
        Assert.Equal(0, Value(_service.ListFindings(null)).Kinds.Sum(k => k.Count));
        Assert.Equal(Outcome.NotFound, _service.DismissFinding(id + 100).Outcome);
    }

    private void Finding(string kind, string playback, Guid item, string device) =>
        _store.SaveFinding(new NewFinding { Kind = kind, PlaybackKey = playback, ItemId = item, Title = "Test Movie", Device = device, Client = "Jellyfin Roku", UserName = "alice", Detail = "what the server saw" });

    private IssueList List(IssueQuery query) => Value(_service.ListIssues(_admin.Id, query));

    private static T Value<T>(ServiceResult<T> result)
    {
        Assert.True(result.Outcome == Outcome.Ok, result.Code);
        return result.Value!;
    }
}
