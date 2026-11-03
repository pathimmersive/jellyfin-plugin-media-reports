using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Notifications;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Session;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class ReporterNotifierTests : IDisposable
{
    private static readonly Guid MovieId = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly List<SessionInfo> _live = [];
    private readonly List<(string SessionId, MessageCommand Message)> _sent = [];
    private readonly ListLogger<ReporterNotifier> _log = new();
    private readonly User _admin = new("admin", "auth", "reset");
    private readonly User _alice = new("alice", "auth", "reset");
    private readonly User _bob = new("bob", "auth", "reset");
    private readonly ReportStore _store;
    private readonly AdminService _service;

    public ReporterNotifierTests()
    {
        Directory.CreateDirectory(_dir);
        var db = new ReportDatabase(Path.Combine(_dir, "media-reports.db"), NullLogger<ReportDatabase>.Instance);
        db.Initialize();
        _store = new ReportStore(db, _clock, () => 365);

        _sessions.SetupGet(m => m.Sessions).Returns(() => _live);
        _sessions
            .Setup(m => m.SendMessageCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageCommand>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, MessageCommand, CancellationToken>((_, session, message, _) => _sent.Add((session, message)))
            .Returns(Task.CompletedTask);

        var users = new Mock<IUserManager>();
        foreach (var user in new[] { _admin, _alice, _bob })
        {
            users.Setup(m => m.GetUserById(user.Id)).Returns(user);
        }

        var notifier = new ReporterNotifier(_sessions.Object, _store, _log);
        _service = new AdminService(_store, Mock.Of<ILibraryManager>(), users.Object, Mock.Of<IMediaSourceManager>(), Mock.Of<IProviderManager>(), Mock.Of<IFileSystem>(), () => new PluginConfiguration(), () => "server-1", notifier);
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
    public void Fixed_SendsOneToastToTheReportersRemoteControlSessionOnly()
    {
        var issue = File(_alice, MovieId).IssueId;
        Connect("phone", _alice, remoteControl: true);
        Connect("android-player", _alice, remoteControl: false);
        Connect("bob-tv", _bob, remoteControl: true);

        Assert.Equal(Outcome.Ok, _service.UpdateIssue(issue, new IssuePatch("Fixed", null), _admin.Id).Outcome);

        var (session, message) = Assert.Single(_sent);
        Assert.Equal("phone", session);
        Assert.Equal(("Media Reports", "Your report on Test Movie is Fixed.", 8000L), (message.Header, message.Text, message.TimeoutMs));
    }

    [Fact]
    public void Closing_WithTheReporterOffline_SendsNothing_AndStoresTheStatus()
    {
        var issue = File(_alice, MovieId).IssueId;

        Assert.Equal(Outcome.Ok, _service.UpdateIssue(issue, new IssuePatch("WontFix", null), _admin.Id).Outcome);

        Assert.Empty(_sent);
        Assert.Equal(IssueStatus.WontFix, _store.GetIssue(issue)!.Status);
    }

    [Fact]
    public void Closing_AnIssueTwoUsersReported_SendsEachTheirOwnNotice_NamingNeitherOther()
    {
        var issue = File(_alice, MovieId).IssueId;
        File(_bob, MovieId);
        Connect("alice-phone", _alice, remoteControl: true);
        Connect("bob-tv", _bob, remoteControl: true);

        Assert.Equal(Outcome.Ok, _service.SetStatuses(new IssueStatusRequest([issue], "CantReproduce"), _admin.Id).Outcome);

        Assert.Equal(["alice-phone", "bob-tv"], _sent.Select(s => s.SessionId).Order());
        Assert.All(_sent, s => Assert.Equal("Your report on Test Movie is Can't reproduce.", s.Message.Text));
        Assert.All(_sent, s => Assert.DoesNotContain("alice", s.Message.Text, StringComparison.OrdinalIgnoreCase));
        Assert.All(_sent, s => Assert.DoesNotContain("bob", s.Message.Text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnEpisode_IsNamedWithItsSeriesSeasonAndEpisode()
    {
        var issue = File(_alice, Guid.NewGuid(), type: "Episode").IssueId;
        Connect("phone", _alice, remoteControl: true);

        _service.UpdateIssue(issue, new IssuePatch("Duplicate", null), _admin.Id);

        Assert.Equal("Your report on Test Series S01E02 is Duplicate.", Assert.Single(_sent).Message.Text);
    }

    [Fact]
    public void Investigating_Reopening_AndMovingBetweenClosedStatuses_SendNothing()
    {
        var issue = File(_alice, MovieId).IssueId;
        _store.SetStatus(issue, IssueStatus.Fixed, _admin.Id);
        Connect("phone", _alice, remoteControl: true);

        _service.UpdateIssue(issue, new IssuePatch("Duplicate", null), _admin.Id);
        _service.UpdateIssue(issue, new IssuePatch("New", null), _admin.Id);
        _service.SetStatuses(new IssueStatusRequest([issue], "Investigating"), _admin.Id);
        _service.UpdateIssue(issue, new IssuePatch(null, "a note"), _admin.Id);

        Assert.Empty(_sent);
        Assert.Equal(IssueStatus.Investigating, _store.GetIssue(issue)!.Status);
    }

    [Fact]
    public void ADeletedReporter_GetsNothing()
    {
        var issue = File(_alice, MovieId).IssueId;
        Connect("phone", _alice, remoteControl: true);
        _store.AnonymiseUser(_alice.Id);

        _service.UpdateIssue(issue, new IssuePatch("Fixed", null), _admin.Id);

        Assert.Empty(_sent);
    }

    [Fact]
    public void ASendThatThrows_LeavesTheStatusStored_AndLogsAWarning()
    {
        var issue = File(_alice, MovieId).IssueId;
        Connect("phone", _alice, remoteControl: true);
        _sessions
            .Setup(m => m.SendMessageCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("socket closed"));

        Assert.Equal(Outcome.Ok, _service.UpdateIssue(issue, new IssuePatch("Fixed", null), _admin.Id).Outcome);

        Assert.Equal(IssueStatus.Fixed, _store.GetIssue(issue)!.Status);
        Assert.Contains(_log.Warnings, w => w.Contains("phone", StringComparison.Ordinal));
    }

    private void Connect(string id, User user, bool remoteControl)
    {
        var session = new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance)
        {
            Id = id,
            UserId = user.Id,
            Capabilities = new ClientCapabilities { SupportsMediaControl = remoteControl },
        };
        if (remoteControl)
        {
            session.AddController(Mock.Of<ISessionController>(c => c.IsSessionActive && c.SupportsMediaControl));
        }

        Assert.Equal(remoteControl, session.SupportsRemoteControl);
        _live.Add(session);
    }

    private FiledReport File(User user, Guid itemId, string type = "Movie")
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        return _store.AddReport(new NewReport
        {
            ReporterUserId = user.Id,
            ReporterName = user.Username,
            ItemId = itemId,
            ItemType = type,
            ItemName = type == "Episode" ? "Pilot" : "Test Movie",
            SeriesName = type == "Episode" ? "Test Series" : null,
            SeasonNumber = type == "Episode" ? 1 : null,
            EpisodeNumber = type == "Episode" ? 2 : null,
            ReasonCode = "stutter",
        });
    }
}
