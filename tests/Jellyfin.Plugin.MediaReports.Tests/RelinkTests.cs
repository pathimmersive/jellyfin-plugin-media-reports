using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Relink;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class RelinkTests : IDisposable
{
    private static readonly DateTimeOffset FileTime = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly FakeLibrary _library = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly ListLogger<LibraryRelink> _logger = new();
    private readonly ReportStore _store;
    private readonly LibraryRelink _relink;

    public RelinkTests()
    {
        Directory.CreateDirectory(_dir);
        var db = new ReportDatabase(Path.Combine(_dir, "media-reports.db"), NullLogger<ReportDatabase>.Instance);
        db.Initialize();
        _store = new ReportStore(db, _clock, () => 365);
        _relink = new LibraryRelink(_libraryManager.Object, _library, _store, _logger);
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
    public void RemovedMovie_MovesOpenAndClosedIssues_ToTheOneMovieWithItsTmdbId()
    {
        var old = Movie(tmdb: "603");
        var open = File(old, "stutter");
        var fixedOne = File(old, "audio");
        _store.SetStatus(fixedOne.IssueId, IssueStatus.Fixed, null);
        var replacement = _library.Add(Movie(tmdb: "603"));

        Assert.Equal(2, _relink.Removed(old.Id));

        var moved = _store.GetIssue(open.IssueId)!;
        Assert.Equal(replacement.Id, moved.ItemId);
        Assert.Equal(IssueStatus.ReplacedVerify, moved.Status);
        var stillFixed = _store.GetIssue(fixedOne.IssueId)!;
        Assert.Equal(replacement.Id, stillFixed.ItemId);
        Assert.Equal(IssueStatus.Fixed, stillFixed.Status);
        Assert.All(new[] { open.IssueId, fixedOne.IssueId }, id =>
            Assert.Contains(_store.GetIssueEvents(id), e => e.Kind == "relinked" && e.Detail!.StartsWith("Moved to the replaced file", StringComparison.Ordinal)));
        Assert.Equal(replacement.Id, _store.GetIssueReports(open.IssueId).Single().ItemId);
    }

    [Theory]
    [InlineData("Imdb", "tt0133093", "TT0133093")]
    [InlineData("Tvdb", "169", " 169 ")]
    public void Movies_MatchOnAnySharedProviderId_IgnoringCaseAndSpaces(string provider, string stored, string library)
    {
        var old = Movie(ids: new() { [provider] = stored });
        var filed = File(old, "stutter");
        var replacement = _library.Add(Movie(ids: new() { [provider] = library, ["Tmdb"] = string.Empty }));

        _relink.Removed(old.Id);

        Assert.Equal(replacement.Id, _store.GetIssue(filed.IssueId)!.ItemId);
    }

    [Fact]
    public void TwoMatches_LeaveTheIssueWhereItWas()
    {
        var old = Movie(tmdb: "603");
        var filed = File(old, "stutter");
        _library.Add(Movie(tmdb: "603"));
        _library.Add(Movie(tmdb: "603"));

        Assert.Equal(0, _relink.Removed(old.Id));

        Assert.Equal(old.Id, _store.GetIssue(filed.IssueId)!.ItemId);
        Assert.Equal(IssueStatus.New, _store.GetIssue(filed.IssueId)!.Status);
    }

    [Fact]
    public void NoProviderIds_NeverMatch()
    {
        var old = Movie(ids: []);
        var filed = File(old, "stutter");
        _library.Add(Movie(ids: []));

        Assert.Equal(0, _relink.Removed(old.Id));
        Assert.Equal(old.Id, _store.GetIssue(filed.IssueId)!.ItemId);
        Assert.Equal(0, _library.Queries);
    }

    [Fact]
    public void ADifferentType_SharingTheId_IsNotAMatch()
    {
        var old = Movie(tmdb: "603");
        var filed = File(old, "stutter");
        _library.Add(Title("Series", new() { ["Tmdb"] = "603" }));

        _relink.Removed(old.Id);

        Assert.Equal(old.Id, _store.GetIssue(filed.IssueId)!.ItemId);
    }

    [Fact]
    public void Episode_MatchesByItsSeriesIdAndNumbers()
    {
        var old = Episode(seriesTvdb: "81189", season: 2, episode: 3);
        var filed = File(old, "audio");
        _library.Add(Episode(seriesTvdb: "81189", season: 2, episode: 4));
        _library.Add(Episode(seriesTvdb: "81189", season: 1, episode: 3));
        var replacement = _library.Add(Episode(seriesTvdb: "81189", season: 2, episode: 3));

        _relink.Removed(old.Id);

        Assert.Equal(replacement.Id, _store.GetIssue(filed.IssueId)!.ItemId);
    }

    [Fact]
    public void Episode_MatchesByItsOwnIdFirst()
    {
        var old = Episode(seriesTvdb: "81189", season: 2, episode: 3, ownTvdb: "349232");
        var filed = File(old, "audio");
        var replacement = _library.Add(Episode(seriesTvdb: "99999", season: 7, episode: 7, ownTvdb: "349232"));
        _library.Add(Episode(seriesTvdb: "81189", season: 2, episode: 3));

        _relink.Removed(old.Id);

        Assert.Equal(replacement.Id, _store.GetIssue(filed.IssueId)!.ItemId);
    }

    [Fact]
    public void Season_MatchesByItsSeriesIdAndNumber()
    {
        var old = Title("Season", [], new() { ["Tvdb"] = "81189" }, season: 2);
        var filed = File(old, "metadata");
        _library.Add(Title("Season", [], new() { ["Tvdb"] = "81189" }, season: 3));
        var replacement = _library.Add(Title("Season", [], new() { ["Tvdb"] = "81189" }, season: 2));

        _relink.Removed(old.Id);

        Assert.Equal(replacement.Id, _store.GetIssue(filed.IssueId)!.ItemId);
    }

    [Fact]
    public void AddedAfterTheRemoval_MovesTheIssuesOfTheTitleThatLeft()
    {
        var old = Movie(tmdb: "603");
        var filed = File(old, "stutter");
        Assert.Equal(0, _relink.Removed(old.Id));

        var replacement = _library.Add(Movie(tmdb: "603"));
        Assert.Equal(1, _relink.Added(replacement));

        Assert.Equal(replacement.Id, _store.GetIssue(filed.IssueId)!.ItemId);
    }

    [Fact]
    public void Added_LeavesIssuesOfATitleStillInTheLibrary()
    {
        var old = _library.Add(Movie(tmdb: "603"));
        var filed = File(old, "stutter");
        var copy = _library.Add(Movie(tmdb: "603"));

        Assert.Equal(0, _relink.Added(copy));

        Assert.Equal(old.Id, _store.GetIssue(filed.IssueId)!.ItemId);
    }

    [Fact]
    public void Moving_ToATitleWithAnOpenIssueForTheSameReason_JoinsIt()
    {
        var old = Movie(tmdb: "603");
        var moved = File(old, "audio");
        var replacement = _library.Add(Movie(tmdb: "603"));
        var existing = File(replacement, "audio");

        _relink.Removed(old.Id);

        Assert.Null(_store.GetIssue(moved.IssueId));
        var joined = _store.GetIssue(existing.IssueId)!;
        Assert.Equal(IssueStatus.ReplacedVerify, joined.Status);
        Assert.Equal(2, _store.GetIssueReports(existing.IssueId).Count);
    }

    [Fact]
    public void FileChange_MarksOpenIssues_AndArmsAgainAfterTheAdminMovesThemOn()
    {
        var movie = _library.Add(Movie(tmdb: "603"), size: 1000, modified: FileTime);
        var open = File(movie, "stutter", size: 1000);
        var fixedOne = File(movie, "audio", size: 1000);
        _store.SetStatus(fixedOne.IssueId, IssueStatus.Fixed, null);

        Assert.Equal(0, _relink.FileSeen(movie.Id));

        _library.SetFile(movie.Id, 2000, FileTime.AddDays(1));
        Assert.Equal(1, _relink.FileSeen(movie.Id));
        Assert.Equal(IssueStatus.ReplacedVerify, _store.GetIssue(open.IssueId)!.Status);
        Assert.Contains(_store.GetIssueEvents(open.IssueId), e => e.Kind == "status changed" && e.Detail == "The file changed" && e.ActorUserId is null);
        Assert.Equal(IssueStatus.Fixed, _store.GetIssue(fixedOne.IssueId)!.Status);

        _library.SetFile(movie.Id, 3000, FileTime.AddDays(2));
        Assert.Equal(0, _relink.FileSeen(movie.Id));
        Assert.Equal(3000, _store.GetIssue(open.IssueId)!.CheckedFileSize);

        _store.SetStatus(open.IssueId, IssueStatus.Investigating, Guid.NewGuid());
        Assert.Equal(0, _relink.FileSeen(movie.Id));
        _library.SetFile(movie.Id, 3000, FileTime.AddDays(3));
        Assert.Equal(1, _relink.FileSeen(movie.Id));
        Assert.Equal(IssueStatus.ReplacedVerify, _store.GetIssue(open.IssueId)!.Status);
    }

    [Fact]
    public void FileChange_OfAnIssueFiledWithoutAFile_OnlyRecordsIt()
    {
        var movie = _library.Add(Movie(tmdb: "603"), size: 1000, modified: FileTime);
        var open = File(movie, "stutter", size: null);

        Assert.Equal(0, _relink.FileSeen(movie.Id));

        Assert.Equal(IssueStatus.New, _store.GetIssue(open.IssueId)!.Status);
        Assert.Equal(1000, _store.GetIssue(open.IssueId)!.CheckedFileSize);
    }

    [Fact]
    public void Reconcile_MovesIssuesOfTitlesThatLeft_AndChecksFiles()
    {
        var gone = Movie(tmdb: "603");
        var movedIssue = File(gone, "stutter");
        var replacement = _library.Add(Movie(tmdb: "603"));
        var changed = _library.Add(Movie(tmdb: "604"), size: 1000, modified: FileTime);
        var changedIssue = File(changed, "audio", size: 1000);
        _library.SetFile(changed.Id, 1500, FileTime);

        var (moved, replaced) = _relink.Reconcile();

        Assert.Equal((1, 1), (moved, replaced));
        Assert.Equal(replacement.Id, _store.GetIssue(movedIssue.IssueId)!.ItemId);
        Assert.Equal(IssueStatus.ReplacedVerify, _store.GetIssue(changedIssue.IssueId)!.Status);
    }

    [Fact]
    public async Task AFailingHandler_IsLogged_AndNeverThrownToTheLibrary()
    {
        await _relink.StartAsync(default);
        var old = Movie(tmdb: "603");
        File(old, "stutter");
        _library.Throw = true;

        var item = new Movie { Id = old.Id };
        _libraryManager.Raise(m => m.ItemRemoved += null, null, new ItemChangeEventArgs { Item = item });
        await _relink.WhenIdle;

        Assert.Single(_logger.Errors);
        await _relink.StopAsync(default);
    }

    [Fact]
    public async Task LibraryEvents_RunOffTheEventThread_AndMoveTheIssues()
    {
        await _relink.StartAsync(default);
        var old = Movie(tmdb: "603");
        var filed = File(old, "stutter");
        var replacement = _library.Add(Movie(tmdb: "603"));

        _libraryManager.Raise(m => m.ItemRemoved += null, null, new ItemChangeEventArgs { Item = new Movie { Id = old.Id } });
        await _relink.WhenIdle;

        Assert.Equal(replacement.Id, _store.GetIssue(filed.IssueId)!.ItemId);
        await _relink.StopAsync(default);
        Assert.Empty(_logger.Errors);
    }

    [Fact]
    public void Task_IsNamedAndRunsDaily()
    {
        var task = new RelinkTask(_relink, new ListLogger<RelinkTask>());

        Assert.Equal("Re-link media reports", task.Name);
        Assert.Equal(MediaBrowser.Model.Tasks.TaskTriggerInfoType.DailyTrigger, task.GetDefaultTriggers().Single().Type);
    }

    private static RelinkTitle Movie(string? tmdb = null, Dictionary<string, string>? ids = null) =>
        Title("Movie", ids ?? new() { ["Tmdb"] = tmdb! });

    private static RelinkTitle Episode(string seriesTvdb, int season, int episode, string? ownTvdb = null) =>
        Title("Episode", ownTvdb is null ? [] : new() { ["Tvdb"] = ownTvdb }, new() { ["Tvdb"] = seriesTvdb }, season, episode);

    private static RelinkTitle Title(string type, Dictionary<string, string> ids, Dictionary<string, string>? seriesIds = null, int? season = null, int? episode = null) =>
        new(Guid.NewGuid(), type, ids, seriesIds ?? [], season, episode);

    private FiledReport File(RelinkTitle title, string reason, long? size = 1000)
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        return _store.AddReport(new NewReport
        {
            ReporterUserId = Guid.NewGuid(),
            ReporterName = "alice",
            ItemId = title.Id,
            ItemType = title.Type,
            ItemName = "Title",
            ProviderIds = title.ProviderIds,
            SeriesProviderIds = title.SeriesProviderIds,
            SeasonNumber = title.Season,
            EpisodeNumber = title.Episode,
            FileSize = size,
            FileModified = size is null ? null : FileTime,
            ReasonCode = reason,
        });
    }

    private sealed class FakeLibrary : IRelinkLibrary
    {
        private readonly Dictionary<Guid, RelinkTitle> _titles = [];
        private readonly Dictionary<Guid, (long, DateTimeOffset)> _files = [];

        public bool Throw { get; set; }

        public int Queries { get; private set; }

        public RelinkTitle Add(RelinkTitle title, long? size = null, DateTimeOffset? modified = null)
        {
            _titles[title.Id] = title;
            if (size is { } s)
            {
                _files[title.Id] = (s, modified!.Value);
            }

            return title;
        }

        public void SetFile(Guid id, long size, DateTimeOffset modified) => _files[id] = (size, modified);

        public bool Contains(Guid itemId) => Throw ? throw new InvalidOperationException("library unavailable") : _titles.ContainsKey(itemId);

        public IReadOnlyList<RelinkTitle> FindByProviderIds(string type, IReadOnlyDictionary<string, string> providerIds)
        {
            Queries++;
            if (Throw)
            {
                throw new InvalidOperationException("library unavailable");
            }

            return _titles.Values.Where(t => t.Type == type).ToList();
        }

        public IReadOnlyList<RelinkTitle> FindInSeries(string type, IReadOnlyDictionary<string, string> seriesProviderIds, int season)
        {
            Queries++;
            return _titles.Values.Where(t => t.Type == type).ToList();
        }

        public (long Size, DateTimeOffset Modified)? FileOf(Guid itemId) =>
            _files.TryGetValue(itemId, out var file) ? file : null;
    }
}
