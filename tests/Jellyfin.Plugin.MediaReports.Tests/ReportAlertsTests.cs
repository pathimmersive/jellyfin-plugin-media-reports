using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Notifications;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class ReportAlertsTests
{
    private const string DiscordHook = "https://discord.example/api/webhooks/1/abc";
    private const string GenericHook = "http://hooks.local/media";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly Receiver _receiver = new();
    private readonly List<ActivityLog> _activity = [];
    private readonly ListLogger<ReportAlerts> _logger = new();
    private readonly PluginConfiguration _config = new() { DiscordWebhookUrl = DiscordHook, WebhookUrl = GenericHook };
    private readonly ReportAlerts _alerts;
    private long _ids;

    public ReportAlertsTests()
    {
        var activity = new Mock<IActivityManager>();
        activity.Setup(m => m.CreateAsync(It.IsAny<ActivityLog>())).Callback<ActivityLog>(a =>
        {
            lock (_activity)
            {
                _activity.Add(a);
            }
        }).Returns(Task.CompletedTask);
        _alerts = new ReportAlerts(() => new HttpClient(_receiver, disposeHandler: false), activity.Object, () => _config, () => "https://jf.example.com/", _time, _logger);
    }

    [Fact]
    public async Task ReportsFiledWithinAMinute_GoOutTogether_OnceToEachWebhook()
    {
        _alerts.Filed(Report());
        _time.Advance(TimeSpan.FromSeconds(10));
        _alerts.Filed(Report(reason: "audio"));
        _time.Advance(TimeSpan.FromSeconds(10));
        _alerts.Filed(Report(reason: "subtitles"));
        _time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.Empty(_receiver.Requests);

        _time.Advance(TimeSpan.FromSeconds(10));
        await _alerts.Sending!;

        Assert.Equal(2, _receiver.Requests.Count);
        using var discord = JsonDocument.Parse(_receiver.Body(DiscordHook));
        using var generic = JsonDocument.Parse(_receiver.Body(GenericHook));
        Assert.Equal(3, discord.RootElement.GetProperty("embeds").GetArrayLength());
        Assert.Equal("3 new reports", discord.RootElement.GetProperty("content").GetString());
        Assert.Equal(["Stutter or jitter", "Audio problem", "Subtitle problem"], generic.RootElement.GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("reason").GetString()));
        Assert.Equal("reports.filed", generic.RootElement.GetProperty("event").GetString());
        Assert.Equal(3, _activity.Count);
    }

    [Fact]
    public async Task AReportAfterAGroupWasSent_StartsANewGroup()
    {
        _alerts.Filed(Report());
        _time.Advance(ReportAlerts.Window);
        await _alerts.Sending!;
        _alerts.Filed(Report());
        _time.Advance(ReportAlerts.Window);
        await _alerts.Sending!;

        Assert.Equal(4, _receiver.Requests.Count);
    }

    [Fact]
    public async Task NoWebhookSet_SendsNothing_AndStillWritesTheActivityLog()
    {
        _config.DiscordWebhookUrl = string.Empty;
        _config.WebhookUrl = " ";

        _alerts.Filed(Report());
        _time.Advance(ReportAlerts.Window);
        await _alerts.Sending!;

        Assert.Empty(_receiver.Requests);
        var entry = Assert.Single(_activity);
        Assert.Equal("alice reported Test Movie: Stutter or jitter", entry.Name);
        Assert.Equal(ReportAlerts.ActivityType, entry.Type);
        Assert.Equal(Report().ItemId.ToString("N"), entry.ItemId);
    }

    [Fact]
    public async Task AFailedSend_IsRetriedOnceAfter30Seconds_ThenDropped()
    {
        _config.DiscordWebhookUrl = string.Empty;
        _receiver.Status = HttpStatusCode.InternalServerError;

        _alerts.Filed(Report());
        _time.Advance(ReportAlerts.Window);
        await Until(() => _receiver.Requests.Count == 1 && _logger.Warnings.Count == 1);
        _time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50);
        Assert.Single(_receiver.Requests);

        _time.Advance(TimeSpan.FromSeconds(1));
        await _alerts.Sending!;
        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);

        Assert.Equal(2, _receiver.Requests.Count);
        Assert.Equal(2, _logger.Warnings.Count);
        Assert.Contains("Answered 500", _logger.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("dropped", _logger.Warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASendWithNoAnswerFor10Seconds_TimesOut()
    {
        _config.DiscordWebhookUrl = string.Empty;
        _receiver.Hang = true;

        _alerts.Filed(Report());
        _time.Advance(ReportAlerts.Window);
        await Until(() => _receiver.Requests.Count == 1);
        _time.Advance(ReportAlerts.SendTimeout);

        await Until(() => _logger.Warnings.Count == 1);
        Assert.Contains("No answer within 10 seconds", _logger.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Discord_ShowsTenReports_AndCountsTheRest()
    {
        var reports = Enumerable.Range(1, 12).Select(i => Alert() with { Title = $"Movie {i}" }).ToList();

        using var body = JsonDocument.Parse(ReportAlerts.Discord(reports));

        Assert.Equal("Media Reports", body.RootElement.GetProperty("username").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("embeds").GetArrayLength());
        Assert.Equal("12 new reports: the first 10 below, and 2 more on the Media Reports page.", body.RootElement.GetProperty("content").GetString());
        var embed = body.RootElement.GetProperty("embeds")[0];
        Assert.Equal("Movie 1", embed.GetProperty("title").GetString());
        Assert.Equal("https://jf.example.com/web/#/configurationpage?name=mediareports&issue=7", embed.GetProperty("url").GetString());
        Assert.Equal(
            ["Reason", "Reported by", "Device", "How it played"],
            embed.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        Assert.Equal("Living Room TV · Jellyfin Android TV", embed.GetProperty("fields")[2].GetProperty("value").GetString());
    }

    [Fact]
    public async Task AnAlert_CutsTheNoteTo300Characters_AndCarriesNoPathAddressOrAdminNote()
    {
        _alerts.Filed(Report(note: new string('x', 900)));
        _time.Advance(ReportAlerts.Window);
        await _alerts.Sending!;

        foreach (var hook in new[] { DiscordHook, GenericHook })
        {
            var body = _receiver.Body(hook);
            Assert.DoesNotContain("/media/movies", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Test Movie (2026).mkv", body, StringComparison.Ordinal);
            Assert.DoesNotContain("192.168.", body, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 300), body, StringComparison.Ordinal);
            Assert.Contains(new string('x', 299) + "…", body, StringComparison.Ordinal);
        }

        using var generic = JsonDocument.Parse(_receiver.Body(GenericHook));
        var report = generic.RootElement.GetProperty("reports")[0];
        Assert.Equal(
            ["issueId", "itemId", "title", "reasonCode", "reason", "user", "device", "client", "playMethod", "transcodeReasons", "howItPlayed", "note", "filedAt", "link"],
            report.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Transcode (VideoCodecNotSupported, AudioCodecNotSupported)", report.GetProperty("howItPlayed").GetString());
        Assert.Equal(["VideoCodecNotSupported", "AudioCodecNotSupported"], report.GetProperty("transcodeReasons").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task TestAlert_SaysPerAddressWhetherItWasAccepted()
    {
        _receiver.StatusFor[GenericHook] = HttpStatusCode.NotFound;

        var results = await _alerts.SendTestAsync(DiscordHook, GenericHook);

        Assert.Equal(("discordWebhookUrl", true, (int?)200), (results[0].Field, results[0].Accepted, results[0].Status));
        Assert.Equal(("webhookUrl", false, (int?)404, "Answered 404"), (results[1].Field, results[1].Accepted, results[1].Status, results[1].Message));
        Assert.Contains("Sample Movie (2026)", _receiver.Body(DiscordHook), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestAlert_SkipsAnEmptyAddress_AndRefusesOneThatIsNotHttp()
    {
        var results = await _alerts.SendTestAsync("ftp://example.com/hook", "  ");

        var result = Assert.Single(results);
        Assert.Equal(("discordWebhookUrl", false, "Enter an http or https address."), (result.Field, result.Accepted, result.Message));
        Assert.Empty(_receiver.Requests);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private static AlertReport Alert() => new()
    {
        IssueId = 7,
        Title = "Test Movie",
        Reason = "Stutter or jitter",
        ReasonCode = "stutter",
        User = "alice",
        Device = "Living Room TV",
        Client = "Jellyfin Android TV",
        HowItPlayed = "Direct play",
        FiledAt = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero),
        Link = "https://jf.example.com/web/#/configurationpage?name=mediareports&issue=7",
    };

    private StoredReport Report(string reason = "stutter", string? note = "Picture keeps jumping back.") => new()
    {
        Id = Interlocked.Increment(ref _ids),
        CreatedAt = _time.GetUtcNow(),
        ReporterUserId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ReporterName = "alice",
        ItemId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        ItemType = "Movie",
        ItemName = "Test Movie",
        FilePath = "/media/movies/Test Movie (2026)/Test Movie (2026).mkv",
        ReasonCode = reason,
        Note = note,
        IssueId = 7,
        SnapshotJson = """{"Client":"Jellyfin Android TV","RemoteEndPoint":"192.168.1.20"}""",
        PlayMethod = "Transcode",
        TranscodeReasons = "VideoCodecNotSupported,AudioCodecNotSupported",
        Client = "Jellyfin Android TV",
        Device = "Living Room TV",
    };

    private sealed class Receiver : HttpMessageHandler
    {
        public ConcurrentQueue<(string Url, string Body)> Requests { get; } = new();

        public Dictionary<string, HttpStatusCode> StatusFor { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public bool Hang { get; set; }

        public string Body(string url) => Requests.Last(r => r.Url == url).Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Enqueue((url, await request.Content!.ReadAsStringAsync(cancellationToken)));
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new HttpResponseMessage(StatusFor.TryGetValue(url, out var status) ? status : Status);
        }
    }
}
