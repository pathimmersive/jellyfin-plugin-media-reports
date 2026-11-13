using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Notifications;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class ButtonWatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly PluginConfiguration _config = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly List<bool> _sent = [];
    private readonly ReportStore _store;
    private readonly ButtonWatch _watch;

    public ButtonWatchTests()
    {
        Directory.CreateDirectory(_dir);
        var db = new ReportDatabase(Path.Combine(_dir, "media-reports.db"), NullLogger<ReportDatabase>.Instance);
        db.Initialize();
        _store = new ReportStore(db, _clock, () => 365);
        _watch = new ButtonWatch(_sessions.Object, _store, () => _config, s => { _sent.Add(s); return Task.CompletedTask; }, _clock, NullLogger<ButtonWatch>.Instance);
    }

    public void Dispose()
    {
        _watch.Dispose();
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
    public void Stopped_AfterSevenDaysWithoutALoad_WhileWebClientsPlayed()
    {
        LastLoadedThirtyDaysAgo_PlayedYesterday();

        Assert.True(Evaluate().Stopped);
    }

    [Fact]
    public void NotStopped_WithALoadToday()
    {
        LastLoadedThirtyDaysAgo_PlayedYesterday();
        _store.RecordLoad("Jellyfin Web", "10.12.0");

        Assert.False(Evaluate().Stopped);
    }

    [Fact]
    public void NotStopped_WithoutWebPlayback()
    {
        _store.RecordLoad("Jellyfin Web", "10.12.0");
        _clock.Advance(TimeSpan.FromDays(8));

        Assert.False(Evaluate().Stopped);
    }

    [Fact]
    public void NotStopped_BeforeSevenDaysOfWatching()
    {
        _store.RecordPlayStart("Jellyfin Web", null);
        _clock.Advance(TimeSpan.FromDays(6));
        _store.RecordPlayStart("Jellyfin Web", null);

        Assert.False(Evaluate().Stopped);

        _clock.Advance(TimeSpan.FromDays(1));
        Assert.True(Evaluate().Stopped);
    }

    [Fact]
    public async Task NotStopped_WhileReportingIsOff_AndTheSevenDaysStartAgainWhenItIsTurnedOn()
    {
        LastLoadedThirtyDaysAgo_PlayedYesterday();
        _config.ReportingEnabled = false;

        Assert.False(Evaluate().Stopped);
        Assert.False((await _watch.CheckAsync()).Stopped);

        _config.ReportingEnabled = true;
        Assert.False((await _watch.CheckAsync()).Stopped);
        _clock.Advance(TimeSpan.FromDays(6));
        _store.RecordPlayStart("Jellyfin Web", null);
        Assert.False((await _watch.CheckAsync()).Stopped);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.True((await _watch.CheckAsync()).Stopped);
        Assert.Equal([true], _sent);
    }

    [Fact]
    public async Task Check_SendsOneMessageOnStopping_NoneWhileStopped_AndOneOnLoadingAgain()
    {
        LastLoadedThirtyDaysAgo_PlayedYesterday();

        await _watch.CheckAsync();
        _clock.Advance(ButtonWatch.Interval);
        await _watch.CheckAsync();
        Assert.Equal([true], _sent);

        _store.RecordLoad("Jellyfin Web", "10.12.0");
        await _watch.CheckAsync();
        await _watch.CheckAsync();
        Assert.Equal([true, false], _sent);
    }

    [Fact]
    public async Task Check_AfterARestart_DoesNotRepeatTheMessage()
    {
        LastLoadedThirtyDaysAgo_PlayedYesterday();
        await _watch.CheckAsync();

        using var restarted = new ButtonWatch(_sessions.Object, _store, () => _config, s => { _sent.Add(s); return Task.CompletedTask; }, _clock, NullLogger<ButtonWatch>.Instance);
        await restarted.CheckAsync();

        Assert.Equal([true], _sent);
    }

    [Fact]
    public void Load_IsRecordedAtMostOnceAMinutePerClient()
    {
        Assert.True(_store.RecordLoad("Jellyfin Web", "10.12.0"));
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(_store.RecordLoad("Jellyfin Web", "10.12.0"));
        Assert.True(_store.RecordLoad("Jellyfin Android", "2.6.3"));
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(_store.RecordLoad("Jellyfin Web", "10.12.1"));

        var web = Assert.Single(_store.GetClientsSeen(), c => c.Client == "Jellyfin Web");
        Assert.Equal("10.12.1", web.Version);
        Assert.Equal(_clock.GetUtcNow(), web.LastLoad);
    }

    [Theory]
    [InlineData("Jellyfin Web", true)]
    [InlineData("Jellyfin Android", true)]
    [InlineData("Jellyfin for WebOS", true)]
    [InlineData("Jellyfin Android TV", false)]
    [InlineData("Jellyfin Roku", false)]
    [InlineData("Swiftfin iOS", false)]
    [InlineData(null, false)]
    public void WebClients_AreTheAppsBuiltOnJellyfinWeb(string? client, bool web) =>
        Assert.Equal(web, ButtonWatch.IsWebClient(client));

    [Fact]
    public async Task PlaybackStarts_AreRecordedForWebClientsOnly()
    {
        await _watch.StartAsync(default);

        _sessions.Raise(m => m.PlaybackStart += null, null, new PlaybackProgressEventArgs { ClientName = "Jellyfin Web" });
        _sessions.Raise(m => m.PlaybackStart += null, null, new PlaybackProgressEventArgs { ClientName = "Jellyfin Roku" });
        await _watch.StopAsync(default);

        var seen = Assert.Single(_store.GetClientsSeen());
        Assert.Equal("Jellyfin Web", seen.Client);
        Assert.Equal(_clock.GetUtcNow(), seen.LastPlay);
        Assert.Null(seen.LastLoad);
    }

    [Fact]
    public void Messages_SayWhatHappenedAndLinkToMediaReports()
    {
        var at = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var (discord, generic) = ReportAlerts.ButtonMessage(true, "http://jf/web/#/configurationpage?name=MediaReports", at);

        var d = JsonNode.Parse(discord)!;
        Assert.Equal("Media Reports", d["username"]!.GetValue<string>());
        Assert.StartsWith(ReportAlerts.ButtonStoppedText, d["content"]!.GetValue<string>(), StringComparison.Ordinal);
        var g = JsonNode.Parse(generic)!;
        Assert.Equal("button.stopped", g["event"]!.GetValue<string>());
        Assert.Equal(ReportAlerts.ButtonStoppedText, g["message"]!.GetValue<string>());
        Assert.Equal("http://jf/web/#/configurationpage?name=MediaReports", g["link"]!.GetValue<string>());
        Assert.Equal("button.loading", JsonNode.Parse(ReportAlerts.ButtonMessage(false, "x", at).Generic)!["event"]!.GetValue<string>());
    }

    [Fact]
    public void Metrics_ShowWhetherItStoppedAndEachClientsLastLoad()
    {
        LastLoadedThirtyDaysAgo_PlayedYesterday();
        var metrics = new ReportMetrics(_store, () => _config, "0.1.0.0", _clock);

        var text = metrics.Render();

        Assert.Contains("media_reports_button_stopped 1\n", text, StringComparison.Ordinal);
        var lastLoad = (_clock.GetUtcNow() - TimeSpan.FromDays(30)).ToUnixTimeSeconds();
        Assert.Contains($"media_reports_button_last_load_timestamp_seconds{{client=\"Jellyfin Web\"}} {lastLoad}\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("button_last_load_timestamp_seconds{client=\"Jellyfin Android\"}", text, StringComparison.Ordinal);
    }

    private ButtonStatus Evaluate() => ButtonWatch.Evaluate(_store, _config, _clock.GetUtcNow());

    // Jellyfin Web last loaded the button 30 days ago, which is also when watching began;
    // Jellyfin Android started a playback yesterday.
    private void LastLoadedThirtyDaysAgo_PlayedYesterday()
    {
        _store.RecordLoad("Jellyfin Web", "10.11.0");
        _clock.Advance(TimeSpan.FromDays(22));
        _clock.Advance(TimeSpan.FromDays(7));
        _store.RecordPlayStart("Jellyfin Android", "2.6.3");
        _clock.Advance(TimeSpan.FromDays(1));
    }
}
