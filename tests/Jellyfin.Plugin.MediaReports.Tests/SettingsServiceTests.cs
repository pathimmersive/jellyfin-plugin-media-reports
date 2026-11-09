using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly ReportStore _store;
    private readonly SettingsService _service;
    private PluginConfiguration _config = new();
    private int _saves;

    public SettingsServiceTests()
    {
        Directory.CreateDirectory(_dir);
        var db = new ReportDatabase(Path.Combine(_dir, "media-reports.db"), NullLogger<ReportDatabase>.Instance);
        db.Initialize();
        _store = new ReportStore(db, _clock, () => 365);
        _service = new SettingsService(_store, () => _config, config =>
        {
            _config = config;
            _saves++;
        });
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
    public void Get_ListsTheReasonsInOrder_WithTheirReportCounts()
    {
        File("stutter");
        File("stutter");
        File("audio");

        var settings = _service.Get();

        Assert.Equal(["wont-play", "playback-error", "poor-quality", "stutter", "audio", "subtitles", "metadata", "other"], settings.Reasons.Select(r => r.Code));
        Assert.Equal(2, settings.Reasons.Single(r => r.Code == "stutter").Reports);
        Assert.Equal(1, settings.Reasons.Single(r => r.Code == "audio").Reports);
        Assert.Equal(0, settings.Reasons.Single(r => r.Code == "other").Reports);
        Assert.True(settings.ReportingEnabled);
        Assert.Equal(365, settings.ReportRetentionDays);
    }

    [Fact]
    public void Update_RenamesMovesRetiresAndAdds_KeepingCodes()
    {
        var reasons = Wanted();
        reasons[3] = reasons[3] with { Label = "Choppy video" };
        reasons[6] = reasons[6] with { Retired = true };
        (reasons[0], reasons[1]) = (reasons[1], reasons[0]);
        reasons.Add(new ReasonUpdate(null, "Wrong language", false));

        var (saved, error) = _service.Update(new SettingsUpdate { Reasons = reasons, ReportingEnabled = false, ReportRetentionDays = Json("30") });

        Assert.Null(error);
        Assert.Equal(1, _saves);
        Assert.Equal(["playback-error", "wont-play", "poor-quality", "stutter", "audio", "subtitles", "metadata", "other", "wrong-language"], saved!.Reasons.Select(r => r.Code));
        Assert.Equal("Choppy video", _config.GetReasons().Single(r => r.Code == "stutter").Label);
        Assert.True(_config.GetReasons().Single(r => r.Code == "metadata").Retired);
        Assert.Equal(Enumerable.Range(0, 9), _config.GetReasons().Select(r => r.Order));
        Assert.False(_config.ReportingEnabled);
        Assert.Equal(30, _config.ReportRetentionDays);
    }

    [Theory]
    [InlineData("", "label-empty")]
    [InlineData("   ", "label-empty")]
    [InlineData("A label that is far longer than forty chars", "label-too-long")]
    [InlineData("AUDIO PROBLEM", "label-duplicate")]
    public void Update_RefusesABadLabel_NamingTheReason_AndSavesNothing(string label, string code)
    {
        var reasons = Wanted();
        reasons[2] = reasons[2] with { Label = label };

        var (saved, error) = _service.Update(new SettingsUpdate { Reasons = reasons, ReportingEnabled = false });

        Assert.Null(saved);
        Assert.Equal(code, error!.Code);
        Assert.Equal("reasons", error.Field);
        Assert.Equal(2, error.ReasonIndex);
        AssertNothingSaved();
    }

    [Fact]
    public void Update_RefusesEveryReasonRetired()
    {
        var reasons = Wanted().Select(r => r with { Retired = true }).ToList();

        var (_, error) = _service.Update(new SettingsUpdate { Reasons = reasons });

        Assert.Equal("no-active-reason", error!.Code);
        Assert.Null(error.ReasonIndex);
        AssertNothingSaved();
    }

    [Fact]
    public void Update_RefusesANewCodeThatStoredReportsUse()
    {
        File("old-reason");
        var reasons = Wanted();
        reasons.Add(new ReasonUpdate("old-reason", "Brought back", false));

        var (_, error) = _service.Update(new SettingsUpdate { Reasons = reasons });

        Assert.Equal("code-in-use", error!.Code);
        Assert.Equal(8, error.ReasonIndex);
        AssertNothingSaved();
    }

    [Fact]
    public void Update_GivesAnAddedReasonACodeNoConfiguredOrStoredReasonHas()
    {
        File("wrong-language");
        var reasons = Wanted();
        reasons.Add(new ReasonUpdate(null, "Wrong language", false));
        reasons.Add(new ReasonUpdate(null, "Audio!", false));

        var (saved, _) = _service.Update(new SettingsUpdate { Reasons = reasons });

        Assert.Equal(["wrong-language-2", "audio-2"], saved!.Reasons.Skip(8).Select(r => r.Code));
    }

    [Fact]
    public void Update_KeepsAReasonTheSaveLeavesOut_SinceThereIsNoDelete()
    {
        var reasons = Wanted().Where(r => r.Code != "metadata").ToList();

        var (saved, error) = _service.Update(new SettingsUpdate { Reasons = reasons });

        Assert.Null(error);
        Assert.Equal("metadata", saved!.Reasons[^1].Code);
        Assert.Equal(8, _config.GetReasons().Length);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"-5\"")]
    [InlineData("\"ten\"")]
    [InlineData("\"\"")]
    [InlineData("1.5")]
    [InlineData("true")]
    public void Update_RefusesARetentionThatIsNotAWholeNumberOfDays(string json)
    {
        var (_, error) = _service.Update(new SettingsUpdate { ReportRetentionDays = JsonDocument.Parse(json).RootElement, ReportingEnabled = false });

        Assert.Equal("retention-invalid", error!.Code);
        Assert.Equal("reportRetentionDays", error.Field);
        AssertNothingSaved();
    }

    [Fact]
    public void Update_AcceptsZeroRetention_WhichKeepsClosedReportsForever()
    {
        var (saved, error) = _service.Update(new SettingsUpdate { ReportRetentionDays = JsonDocument.Parse("0").RootElement });

        Assert.Null(error);
        Assert.Equal(0, saved!.ReportRetentionDays);
    }

    [Theory]
    [InlineData("ftp://example.com/hook", "discordWebhookUrl")]
    [InlineData("not an address", "webhookUrl")]
    public void Update_RefusesAWebhookThatIsNotHttp(string address, string field)
    {
        var update = field == "webhookUrl" ? new SettingsUpdate { WebhookUrl = address } : new SettingsUpdate { DiscordWebhookUrl = address };

        var (_, error) = _service.Update(update);

        Assert.Equal("webhook-invalid", error!.Code);
        Assert.Equal(field, error.Field);
        AssertNothingSaved();
    }

    [Fact]
    public void Update_KeepsEveryFieldTheSaveLeavesOut()
    {
        _config = new PluginConfiguration
        {
            SuspectFreezing = false,
            SuspectLowQuality = false,
            DiscordWebhookUrl = "https://discord.example/hook",
            WebhookUrl = "http://hooks.local/media",
            PlaybackRetentionHours = 6,
            ReportRetentionDays = 90,
            ReportingEnabled = false,
        };
        var before = _config;

        var (_, error) = _service.Update(new SettingsUpdate());

        Assert.Null(error);
        foreach (var property in typeof(PluginConfiguration).GetProperties())
        {
            var was = property.GetValue(before);
            var now = property.GetValue(_config);
            if (was is ReasonEntry[] reasons)
            {
                Assert.Equal(reasons, (ReasonEntry[])now!);
            }
            else
            {
                Assert.True(Equals(was, now), property.Name);
            }
        }
    }

    [Theory]
    [InlineData("Wrong language", "wrong-language")]
    [InlineData("  Won't play!! ", "won-t-play")]
    [InlineData("Ünïcode ✓", "n-code")]
    [InlineData("✓✓", "reason")]
    public void NewCode_IsMadeFromTheLabel(string label, string code) =>
        Assert.Equal(code, SettingsService.NewCode(label, new HashSet<string>()));

    private List<ReasonUpdate> Wanted() =>
        _config.GetReasons().Select(r => new ReasonUpdate(r.Code, r.Label, r.Retired)).ToList();

    private void AssertNothingSaved()
    {
        Assert.Equal(0, _saves);
        Assert.Equal(new PluginConfiguration().GetReasons(), _config.GetReasons());
        Assert.True(_config.ReportingEnabled);
        Assert.Equal(365, _config.ReportRetentionDays);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement;

    private void File(string reason)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        _store.AddReport(new NewReport
        {
            ReporterUserId = Guid.NewGuid(),
            ReporterName = "alice",
            ItemId = Guid.NewGuid(),
            ItemType = "Movie",
            ItemName = "Test Movie",
            ReasonCode = reason,
        });
    }
}
