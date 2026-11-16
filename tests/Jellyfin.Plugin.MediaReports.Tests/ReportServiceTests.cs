using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaReports;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Playback;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    private static readonly Guid SongId = Guid.NewGuid();

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

        var movie = new Movie { Id = MovieId, Name = "Test Movie", Path = _moviePath, ProductionYear = 2026, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "693134" } };
        var series = new Series { Id = SeriesId, Name = "Test Series", Path = _dir, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189" } };
        var episode = new Episode { Id = EpisodeId, Name = "Pilot", SeriesId = SeriesId, SeriesName = "Test Series", ParentIndexNumber = 2, IndexNumber = 5, ProductionYear = 2021 };
        foreach (var user in new[] { _alice, _bob })
        {
            _users.Setup(m => m.GetUserById(user.Id)).Returns(user);
            _library.Setup(m => m.GetItemById<BaseItem>(MovieId, user)).Returns(movie);
            _library.Setup(m => m.GetItemById<BaseItem>(SeriesId, user)).Returns(series);
            _library.Setup(m => m.GetItemById<BaseItem>(EpisodeId, user)).Returns(episode);
            _library.Setup(m => m.GetItemById<BaseItem>(SongId, user)).Returns(new MediaBrowser.Controller.Entities.Audio.Audio { Id = SongId, Name = "Test Song" });
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
        Assert.Equal("new", open.Status);
    }

    [Fact]
    public void File_StoresThePlaybackTheServerChose_AndGroupsWithOtherUsers()
    {
        Play("tv", _alice, "Living Room TV", transcoding: true);
        Stop("tv", _alice, "Living Room TV");

        var first = Ok(_service.File(_alice.Id, "Phone-id", new FileReportRequest(MovieId, "stutter", "  keeps jumping back  ")));
        var second = Ok(_service.File(_bob.Id, null, new FileReportRequest(MovieId, "stutter", null)));

        Assert.Equal(first.IssueId, second.IssueId);
        Assert.Equal("new", first.Status);
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
    public void Context_Mine_AndDetail_GiveTheTitleInParts_WithJellyfinsNames()
    {
        var episode = Ok(_service.GetContext(_alice.Id, null, EpisodeId));
        Assert.Equal(("Test Series", 2, 5, 2021), (episode.SeriesName, episode.ParentIndexNumber, episode.IndexNumber, episode.ProductionYear));
        var movie = Ok(_service.GetContext(_alice.Id, null, MovieId));
        Assert.Equal((null, null, null, 2026), (movie.SeriesName, movie.ParentIndexNumber, movie.IndexNumber, movie.ProductionYear));

        var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(EpisodeId, "subtitles", null)));
        Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));

        var rows = Ok(_service.Mine(_alice.Id)).Reports;
        var row = rows.Single(r => r.Id == filed.Id);
        Assert.Equal(("Test Series", 2, 5, 2021), (row.SeriesName, row.ParentIndexNumber, row.IndexNumber, row.ProductionYear));
        var movieRow = rows.Single(r => r.Id != filed.Id);
        Assert.Equal((null, null, null, 2026), (movieRow.SeriesName, movieRow.ParentIndexNumber, movieRow.IndexNumber, movieRow.ProductionYear));
        var detail = Ok(_service.MineDetail(_alice.Id, filed.Id));
        Assert.Equal(("Test Series", 2, 5, 2021), (detail.SeriesName, detail.ParentIndexNumber, detail.IndexNumber, detail.ProductionYear));
        Assert.Equal("Test Series S02E05", detail.Title);
    }

    [Fact]
    public void Context_GivesTheNoteLimit_ThatFilingEnforces()
    {
        var context = Ok(_service.GetContext(_alice.Id, null, MovieId));

        Assert.Equal(1000, context.NoteMaxLength);
        Assert.Equal(Outcome.Ok, _service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", new string('a', context.NoteMaxLength))).Outcome);
        AssertRefused(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "audio", new string('a', context.NoteMaxLength + 1))), Outcome.BadRequest, "note-too-long");
    }

    [Fact]
    public void Mine_PagesFiftyAtATime_NewestFirst_AndCountsUnreadAcrossEveryPage()
    {
        var ids = new List<long>();
        for (var i = 0; i < 120; i++)
        {
            ids.Add(_store.AddReport(new NewReport { ReporterUserId = _alice.Id, ItemId = Guid.NewGuid(), ItemType = "Movie", ItemName = $"Movie {i}", ReasonCode = "stutter" }).ReportId);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _store.SetStatus(_store.GetReport(ids[0])!.IssueId, IssueStatus.Fixed, null);

        var first = Ok(_service.Mine(_alice.Id));
        Assert.Equal((1, 50, 120, 1), (first.Page, first.PageSize, first.Total, first.Unread));
        Assert.Equal(ids.AsEnumerable().Reverse().Take(50), first.Reports.Select(r => r.Id));
        var third = Ok(_service.Mine(_alice.Id, 3));
        Assert.Equal((3, 20, 1), (third.Page, third.Reports.Count, third.Unread));
        Assert.Equal(ids[0], third.Reports[^1].Id);
        Assert.Empty(Ok(_service.Mine(_alice.Id, 4)).Reports);
        AssertRefused(_service.Mine(_alice.Id, 0), Outcome.BadRequest, "bad-query");
    }

    [Fact]
    public async Task Filing_AnswersWithTheLocationOfTheNewReport()
    {
        var authorization = new Mock<IAuthorizationContext>();
        authorization.Setup(m => m.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(new AuthorizationInfo { User = _alice, DeviceId = "phone-id" });
        var http = new DefaultHttpContext();
        http.Request.PathBase = "/jellyfin";
        var controller = new ReportController(_service, authorization.Object) { ControllerContext = new ControllerContext { HttpContext = http } };

        var answer = (await controller.File(new FileReportRequest(MovieId, "stutter", null))).Result as ObjectResult;

        Assert.Equal(StatusCodes.Status201Created, answer!.StatusCode);
        var filed = Assert.IsType<FiledReportView>(answer.Value);
        Assert.Equal($"/jellyfin/MediaReports/Report/Mine/{filed.Id}", http.Response.Headers.Location.ToString());

        var refused = (await controller.File(new FileReportRequest(MovieId, "stutter", null))).Result as ObjectResult;
        Assert.Equal(StatusCodes.Status409Conflict, refused!.StatusCode);
    }

    [Fact]
    public void TheHourlyLimit_SaysWhenAFilingIsAcceptedAgain()
    {
        for (var i = 0; i < ReportService.MaxReportsPerHour; i++)
        {
            var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
            Assert.Equal(Outcome.Ok, _service.Withdraw(_alice.Id, filed.Id).Outcome);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var refused = _service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null));
        AssertRefused(refused, Outcome.TooManyRequests, "too-many-reports");
        Assert.Equal(1800, refused.RetryAfterSeconds);

        _clock.Advance(TimeSpan.FromSeconds(1799));
        Assert.Equal(1, _service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)).RetryAfterSeconds);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Outcome.Ok, _service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)).Outcome);
    }

    [Fact]
    public void AlreadyReported_NamesTheOpenReport_AndNoOtherRefusalNamesOne()
    {
        var first = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));

        var again = _service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null));
        AssertRefused(again, Outcome.Conflict, "already-reported");
        Assert.Equal(first.Id, again.ReportId);
        Assert.Equal(Outcome.Ok, _service.MineDetail(_alice.Id, again.ReportId!.Value).Outcome);

        Assert.Null(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "nonsense", null)).ReportId);
        Assert.Null(_service.File(_bob.Id, null, new FileReportRequest(MovieId, "audio", new string('a', 1001))).ReportId);
    }

    [Fact]
    public async Task Refusals_CarryRetryAfter_AndTheOpenReportsId_OverHttp()
    {
        var authorization = new Mock<IAuthorizationContext>();
        authorization.Setup(m => m.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(new AuthorizationInfo { User = _alice, DeviceId = "phone-id" });
        ReportController Controller(out DefaultHttpContext http)
        {
            http = new DefaultHttpContext();
            return new ReportController(_service, authorization.Object) { ControllerContext = new ControllerContext { HttpContext = http } };
        }

        var filed = (await Controller(out _).File(new FileReportRequest(MovieId, "stutter", null))).Result as ObjectResult;
        var duplicate = (await Controller(out _).File(new FileReportRequest(MovieId, "stutter", null))).Result as ObjectResult;
        Assert.Equal(StatusCodes.Status409Conflict, duplicate!.StatusCode);
        Assert.Equal(((FiledReportView)filed!.Value!).Id, Assert.IsType<ApiError>(duplicate.Value).ReportId);
        Assert.DoesNotContain("ReportId", JsonSerializer.Serialize(new ApiError("note-too-long", "Too long.")), StringComparison.Ordinal);

        // The first filing counts; 29 more, each withdrawn, reach the limit.
        for (var i = 1; i < ReportService.MaxReportsPerHour; i++)
        {
            var other = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "other", null)));
            Assert.Equal(Outcome.Ok, _service.Withdraw(_alice.Id, other.Id).Outcome);
        }

        var tooMany = (await Controller(out var http).File(new FileReportRequest(MovieId, "audio", null))).Result as ObjectResult;
        Assert.Equal(StatusCodes.Status429TooManyRequests, tooMany!.StatusCode);
        Assert.Equal("3600", http.Response.Headers.RetryAfter.ToString());
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
    public void ATitleOfAnotherType_IsRefusedAsNotReportable_AndNothingIsStored()
    {
        AssertRefused(_service.GetContext(_alice.Id, null, SongId), Outcome.BadRequest, "item-not-reportable");
        AssertRefused(_service.File(_alice.Id, null, new FileReportRequest(SongId, "audio", null)), Outcome.BadRequest, "item-not-reportable");
        AssertRefused(_service.File(_alice.Id, null, new FileReportRequest(HiddenId, "audio", null)), Outcome.NotFound, "item-not-found");
        Assert.Equal(0, _store.GetReportCount());
        Assert.Equal(["Movie", "Episode", "Season", "Series"], ReportService.ReportableTypes);
    }

    [Fact]
    public void OpeningAReport_LeavesItUnread_UntilSeenIsCalled()
    {
        var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(filed.IssueId, IssueStatus.Fixed, null);
        Assert.Equal(1, Ok(_service.Mine(_alice.Id)).Unread);

        Ok(_service.MineDetail(_alice.Id, filed.Id));
        Assert.Equal(1, Ok(_service.Mine(_alice.Id)).Unread);

        Assert.Equal(Outcome.Ok, _service.MarkSeen(_alice.Id, filed.Id).Outcome);
        Assert.Equal(0, Ok(_service.Mine(_alice.Id)).Unread);
        AssertRefused(_service.MarkSeen(_bob.Id, filed.Id), Outcome.NotFound, "report-not-found");
    }

    [Fact]
    public void File_InParallel_StoresOneReportPerItemAndReason()
    {
        var results = FileInParallel(20, _ => new FileReportRequest(MovieId, "stutter", null));

        Assert.Equal(1, results.Count(r => r.Outcome == Outcome.Ok));
        Assert.All(results.Where(r => r.Outcome != Outcome.Ok), r => Assert.Equal("already-reported", r.Code));
        Assert.Equal(1, _store.GetReportCount());
    }

    [Fact]
    public void File_InParallel_NeverPassesTheHourlyLimit()
    {
        for (var i = 0; i < ReportService.MaxReportsPerHour - 1; i++)
        {
            var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
            Assert.Equal(Outcome.Ok, _service.Withdraw(_alice.Id, filed.Id).Outcome);
        }

        var reasons = PluginConfiguration.DefaultReasons().Select(r => r.Code).ToArray();
        var results = FileInParallel(reasons.Length, n => new FileReportRequest(MovieId, reasons[n], null));

        Assert.Equal(1, results.Count(r => r.Outcome == Outcome.Ok));
        Assert.All(results.Where(r => r.Outcome != Outcome.Ok), r => Assert.Equal("too-many-reports", r.Code));
    }

    [Fact]
    public async Task FileAsync_WaitsForTheUsersGate_WithoutHoldingAThread()
    {
        var gate = _service.FilingGate(_alice.Id);
        gate.Wait();
        var calls = Task.Run(() => Enumerable.Range(0, 200)
            .Select(_ => _service.FileAsync(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)))
            .ToList());

        Assert.True(calls.Wait(TimeSpan.FromSeconds(5)), "a filing held its caller's thread while the gate was taken");
        Assert.All(calls.Result, t => Assert.False(t.IsCompleted));
        gate.Release();
        var results = await Task.WhenAll(calls.Result);

        Assert.Equal(1, results.Count(r => r.Outcome == Outcome.Ok));
    }

    [Fact]
    public void FileAsync_RefusesAUserAtTheLimit_WithoutWaitingForTheGate()
    {
        for (var i = 0; i < ReportService.MaxReportsPerHour; i++)
        {
            var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
            Assert.Equal(Outcome.Ok, _service.Withdraw(_alice.Id, filed.Id).Outcome);
        }

        var gate = _service.FilingGate(_alice.Id);
        gate.Wait();
        try
        {
            var call = Task.Factory.StartNew(() => _service.FileAsync(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            Assert.True(call.Wait(TimeSpan.FromSeconds(5)) && call.Result.IsCompleted, "a filing past the limit waited for the gate");
            AssertRefused(call.Result.Result, Outcome.TooManyRequests, "too-many-reports");
        }
        finally
        {
            gate.Release();
        }
    }

    private List<ServiceResult<FiledReportView>> FileInParallel(int count, Func<int, FileReportRequest> request)
    {
        using var start = new Barrier(count);
        var tasks = Enumerable.Range(0, count).Select(n => Task.Run(() =>
        {
            start.SignalAndWait();
            return _service.File(_alice.Id, null, request(n));
        })).ToArray();
        Task.WaitAll(tasks);
        return tasks.Select(t => t.Result).ToList();
    }

    [Fact]
    public void File_RefusesThe31stReportInAnHour_CountingWithdrawnOnes_AndAlertsNothing()
    {
        var alerted = 0;
        var service = new ReportService(_library.Object, _users.Object, _capture, _store, () => _config, _clock, _ => alerted++);
        var first = _clock.GetUtcNow();
        for (var i = 0; i < ReportService.MaxReportsPerHour; i++)
        {
            var filed = Ok(service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
            Assert.Equal(Outcome.Ok, service.Withdraw(_alice.Id, filed.Id).Outcome);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        AssertRefused(service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)), Outcome.TooManyRequests, "too-many-reports");
        Assert.Equal(0, _store.GetReportCount());
        Assert.Equal(ReportService.MaxReportsPerHour, alerted);
        Ok(service.File(_bob.Id, null, new FileReportRequest(MovieId, "stutter", null)));

        _clock.Advance(first + TimeSpan.FromHours(1) - _clock.GetUtcNow());
        Ok(service.File(_alice.Id, null, new FileReportRequest(MovieId, "audio", null)));
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

    [Fact]
    public void Mine_ListsOnlyTheCallersReportsNewestFirst_WithTitlesAndReporterLabels()
    {
        var movie = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var episode = Ok(_service.File(_alice.Id, null, new FileReportRequest(EpisodeId, "audio", null)));
        Ok(_service.File(_bob.Id, null, new FileReportRequest(SeriesId, "audio", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(episode.IssueId, IssueStatus.Investigating, _bob.Id);

        var mine = Ok(_service.Mine(_alice.Id));

        Assert.Equal([episode.Id, movie.Id], mine.Reports.Select(r => r.Id));
        Assert.Equal(("Test Series S02E05", "Being looked at"), (mine.Reports[0].Title, mine.Reports[0].StatusLabel));
        Assert.Equal(("Test Movie", "New", "Stutter or jitter"), (mine.Reports[1].Title, mine.Reports[1].StatusLabel, mine.Reports[1].ReasonLabel));
    }

    [Fact]
    public void ReporterEndpoints_GiveReplacedVerifyAsInvestigating_NeverTheStorageName()
    {
        var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        _store.SetStatus(filed.IssueId, IssueStatus.ReplacedVerify, null);

        var row = Ok(_service.Mine(_alice.Id)).Reports[0];
        var detail = Ok(_service.MineDetail(_alice.Id, filed.Id));
        var open = Ok(_service.GetContext(_alice.Id, null, MovieId)).OpenReports[0];

        Assert.Equal(("investigating", "investigating", "investigating"), (row.Status, detail.Status, open.Status));
        Assert.Equal("Being looked at", row.StatusLabel);
    }

    [Fact]
    public void StatusCode_MapsEveryStatusIntoTheDocumentedSet()
    {
        var expected = new Dictionary<IssueStatus, string>
        {
            [IssueStatus.New] = "new",
            [IssueStatus.Investigating] = "investigating",
            [IssueStatus.ReplacedVerify] = "investigating",
            [IssueStatus.Fixed] = "fixed",
            [IssueStatus.WontFix] = "wont-fix",
            [IssueStatus.Duplicate] = "duplicate",
            [IssueStatus.CantReproduce] = "cant-reproduce",
        };

        Assert.Equal(Enum.GetValues<IssueStatus>(), expected.Keys);
        foreach (var (status, code) in expected)
        {
            Assert.Equal(code, ReporterText.StatusCode(status));
        }
    }

    [Fact]
    public void Mine_MarksAReportUnreadWhenItsStatusChanges_AndSeenClearsTheMark()
    {
        var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        Assert.Equal(0, Ok(_service.Mine(_alice.Id)).Unread);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(filed.IssueId, IssueStatus.Fixed, _bob.Id);
        var mine = Ok(_service.Mine(_alice.Id));
        Assert.Equal((1, true, "Fixed"), (mine.Unread, mine.Reports[0].Unread, mine.Reports[0].StatusLabel));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Outcome.Ok, _service.MarkSeen(_alice.Id, filed.Id).Outcome);
        mine = Ok(_service.Mine(_alice.Id));
        Assert.Equal((0, false), (mine.Unread, mine.Reports[0].Unread));
    }

    [Fact]
    public void MineDetail_GivesReasonNoteDeviceAndHistory_ButNotTheAdminsNotes()
    {
        Play("tv", _alice, "Living Room TV");
        var filed = Ok(_service.File(_alice.Id, "tv-id", new FileReportRequest(MovieId, "stutter", "Jumps back every few seconds")));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.UpdateIssue(filed.IssueId, IssueStatus.Investigating, "Secret admin note", _bob.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.UpdateIssue(filed.IssueId, IssueStatus.WontFix, "Resolution: the source is bad", _bob.Id);

        var detail = Ok(_service.MineDetail(_alice.Id, filed.Id));

        Assert.Equal(("Stutter or jitter", "Jumps back every few seconds", "Living Room TV"), (detail.ReasonLabel, detail.Note, detail.Device));
        Assert.Equal(("wont-fix", "Won't fix", false), (detail.Status, detail.StatusLabel, detail.CanWithdraw));
        Assert.Equal(["Reported", "Being looked at", "Won't fix"], detail.History.Select(h => h.Label));
        Assert.Equal([null, "investigating", "wont-fix"], detail.History.Select(h => h.Status));
        Assert.Equal("stutter", detail.ReasonCode);
        Assert.Equal("stutter", Ok(_service.Mine(_alice.Id)).Reports[0].ReasonCode);
        var json = JsonSerializer.Serialize(detail);
        Assert.DoesNotContain("Secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Resolution", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MineDetail_AndWithdraw_OfAnotherUsersReport_AreNotFound_AndChangeNothing()
    {
        var bobs = Ok(_service.File(_bob.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(bobs.IssueId, IssueStatus.Fixed, _alice.Id);

        AssertRefused(_service.MineDetail(_alice.Id, bobs.Id), Outcome.NotFound, "report-not-found");
        AssertRefused(_service.Withdraw(_alice.Id, bobs.Id), Outcome.NotFound, "report-not-found");
        AssertRefused(_service.MineDetail(_alice.Id, 9999), Outcome.NotFound, "report-not-found");

        Assert.NotNull(_store.GetReport(bobs.Id));
        Assert.True(Ok(_service.Mine(_bob.Id)).Reports[0].Unread);
    }

    [Fact]
    public void Withdraw_RemovesANewReport_AndRefusesOneBeingLookedAt()
    {
        var fresh = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        var worked = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "audio", null)));
        _store.SetStatus(worked.IssueId, IssueStatus.Investigating, _bob.Id);

        Ok(_service.Withdraw(_alice.Id, fresh.Id));
        AssertRefused(_service.Withdraw(_alice.Id, worked.Id), Outcome.Conflict, "not-new");

        Assert.Null(_store.GetReport(fresh.Id));
        Assert.Equal([worked.Id], Ok(_service.Mine(_alice.Id)).Reports.Select(r => r.Id));
    }

    [Fact]
    public void ReportingOff_StillListsAndOpensTheCallersReports_ButRefusesWithdraw()
    {
        var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        _config.ReportingEnabled = false;

        Assert.Single(Ok(_service.Mine(_alice.Id)).Reports);
        Ok(_service.MineDetail(_alice.Id, filed.Id));
        AssertRefused(_service.Withdraw(_alice.Id, filed.Id), Outcome.Forbidden, "reporting-off");
        Assert.NotNull(_store.GetReport(filed.Id));
    }

    [Fact]
    public void Mine_GivesAClosedReportItsExpiry_FromTheRetentionPeriod_AndNoneWhileOpenOrWhenPurgingIsOff()
    {
        var open = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "audio", null)));
        var closed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(closed.IssueId, IssueStatus.Fixed, _bob.Id);
        var closedAt = _clock.GetUtcNow();

        var row = Ok(_service.Mine(_alice.Id)).Reports.Single(r => r.Id == closed.Id);
        Assert.Equal((closedAt.AddDays(365), "Expires in 12 months"), (row.ExpiresAt, row.ExpiresLabel));
        Assert.Equal((true, false), (row.CanHide, row.CanWithdraw));
        var openRow = Ok(_service.Mine(_alice.Id)).Reports.Single(r => r.Id == open.Id);
        Assert.Equal((null, false, true), (openRow.ExpiresAt, openRow.CanHide, openRow.CanWithdraw));

        _config.ReportRetentionDays = 30;
        Assert.Equal("Expires in 30 days", Ok(_service.Mine(_alice.Id)).Reports.Single(r => r.Id == closed.Id).ExpiresLabel);

        _config.ReportRetentionDays = 0;
        row = Ok(_service.Mine(_alice.Id)).Reports.Single(r => r.Id == closed.Id);
        Assert.Null(row.ExpiresAt);
        Assert.Null(row.ExpiresLabel);
    }

    [Theory]
    [InlineData(400, "Expires in 13 months")]
    [InlineData(60, "Expires in 2 months")]
    [InlineData(59.5, "Expires in 60 days")]
    [InlineData(1.5, "Expires in 2 days")]
    [InlineData(1, "Expires in 1 day")]
    [InlineData(0.5, "Expires today")]
    [InlineData(-1, "Expires today")]
    public void ExpiryLabel_CountsMonthsFromSixtyDays_DaysBelow_AndTodayUnderOneDay(double days, string label)
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(label, ReporterText.ExpiryLabel(now.AddDays(days), now));
    }

    [Fact]
    public void Hide_RemovesAClosedReportFromTheCallersList_Only()
    {
        var alices = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        var bobs = Ok(_service.File(_bob.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(alices.IssueId, IssueStatus.Fixed, _bob.Id);
        Assert.Equal(1, Ok(_service.Mine(_alice.Id)).Unread);
        var events = _store.GetIssueEvents(alices.IssueId).Count;

        Ok(_service.Hide(_alice.Id, alices.Id));

        var mine = Ok(_service.Mine(_alice.Id));
        Assert.Equal((0, 0), (mine.Reports.Count, mine.Unread));
        Assert.Equal([bobs.Id], Ok(_service.Mine(_bob.Id)).Reports.Select(r => r.Id));
        Assert.Equal(IssueStatus.Fixed, _store.GetIssue(alices.IssueId)!.Status);
        Assert.Equal(events, _store.GetIssueEvents(alices.IssueId).Count);
    }

    [Fact]
    public void Hide_RefusesAnOpenReport_AndAnotherUsersReport()
    {
        var open = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        var bobs = Ok(_service.File(_bob.Id, null, new FileReportRequest(MovieId, "audio", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(bobs.IssueId, IssueStatus.Fixed, _alice.Id);

        AssertRefused(_service.Hide(_alice.Id, open.Id), Outcome.Conflict, "not-closed");
        AssertRefused(_service.Hide(_alice.Id, bobs.Id), Outcome.NotFound, "report-not-found");
        AssertRefused(_service.Hide(_alice.Id, 9999), Outcome.NotFound, "report-not-found");

        Assert.Single(Ok(_service.Mine(_alice.Id)).Reports);
        Assert.Single(Ok(_service.Mine(_bob.Id)).Reports);
    }

    [Fact]
    public void HideClosed_RemovesEveryClosedReport_AndKeepsTheOpenOnes()
    {
        var fixedOne = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        var wontFix = Ok(_service.File(_alice.Id, null, new FileReportRequest(EpisodeId, "audio", null)));
        var open = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "audio", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(fixedOne.IssueId, IssueStatus.Fixed, _bob.Id);
        _store.SetStatus(wontFix.IssueId, IssueStatus.WontFix, _bob.Id);

        Assert.Equal(2, Ok(_service.HideClosed(_alice.Id)).Hidden);
        Assert.Equal([open.Id], Ok(_service.Mine(_alice.Id)).Reports.Select(r => r.Id));
        Assert.Equal(0, Ok(_service.HideClosed(_alice.Id)).Hidden);
    }

    [Fact]
    public void AHiddenReport_ComesBackUnread_WhenItsIssueIsReopened()
    {
        var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(filed.IssueId, IssueStatus.Fixed, _bob.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Ok(_service.MineDetail(_alice.Id, filed.Id));
        Ok(_service.Hide(_alice.Id, filed.Id));
        Assert.Empty(Ok(_service.Mine(_alice.Id)).Reports);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(filed.IssueId, IssueStatus.Investigating, _bob.Id);

        var mine = Ok(_service.Mine(_alice.Id));
        Assert.Equal((1, filed.Id, true), (mine.Unread, mine.Reports[0].Id, mine.Reports[0].Unread));
        AssertRefused(_service.Hide(_alice.Id, filed.Id), Outcome.Conflict, "not-closed");
    }

    [Fact]
    public void ReportingOff_StillLetsTheCallerClearTheirList()
    {
        var filed = Ok(_service.File(_alice.Id, null, new FileReportRequest(MovieId, "stutter", null)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.SetStatus(filed.IssueId, IssueStatus.Fixed, _bob.Id);
        _config.ReportingEnabled = false;

        Ok(_service.Hide(_alice.Id, filed.Id));
        Assert.Equal(0, Ok(_service.HideClosed(_alice.Id)).Hidden);
        Assert.Empty(Ok(_service.Mine(_alice.Id)).Reports);
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
