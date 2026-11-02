using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using Jellyfin.Plugin.MediaReports.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public class ConfigurationTests
{
    private static readonly string[] DefaultCodes = ["wont-play", "playback-error", "poor-quality", "stutter", "audio", "subtitles", "metadata", "other"];

    [Fact]
    public void FreshInstall_HasTheValidatedDefaults()
    {
        var config = new PluginConfiguration();

        Assert.True(config.ReportingEnabled);
        Assert.Equal(DefaultCodes, config.GetReasons().Select(r => r.Code));
        Assert.Equal(
            ["Won't play", "Playback error", "Poor quality", "Stutter or jitter", "Audio problem", "Subtitle problem", "Wrong metadata", "Other"],
            config.GetReasons().Select(r => r.Label));
        Assert.All(config.GetReasons(), r => Assert.False(r.Retired));
        Assert.True(config.SuspectFreezing);
        Assert.True(config.SuspectSlowTranscode);
        Assert.True(config.SuspectLowQuality);
        Assert.True(config.SuspectPlaybackError);
        Assert.Equal(string.Empty, config.DiscordWebhookUrl);
        Assert.Equal(string.Empty, config.WebhookUrl);
        Assert.Equal(365, config.ReportRetentionDays);
        Assert.Equal(24, config.PlaybackRetentionHours);
    }

    [Fact]
    public void SavedReasons_RoundTripWithoutDuplicates()
    {
        var config = new PluginConfiguration
        {
            Reasons =
            [
                new ReasonEntry { Code = "stutter", Label = "Choppy video", Order = 0 },
                new ReasonEntry { Code = "metadata", Label = "Wrong metadata", Order = 1, Retired = true },
                new ReasonEntry { Code = "wrong-language", Label = "Wrong language", Order = 2 },
            ],
        };

        var loaded = RoundTrip(Save(config));

        Assert.Equal(["stutter", "metadata", "wrong-language"], loaded.GetReasons().Select(r => r.Code));
        Assert.Equal("Choppy video", loaded.GetReasons()[0].Label);
        Assert.True(loaded.GetReasons()[1].Retired);
        Assert.Equal(DefaultCodes, RoundTrip(Save(new PluginConfiguration())).GetReasons().Select(r => r.Code));
    }

    [Fact]
    public void OlderFileWithoutNewSettings_LoadsTheirDefaults_AndKeepsTheRest()
    {
        const string Older = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <PlaybackRetentionHours>12</PlaybackRetentionHours>
              <ReportRetentionDays>30</ReportRetentionDays>
            </PluginConfiguration>
            """;

        var loaded = RoundTrip(Older);

        Assert.Equal(12, loaded.PlaybackRetentionHours);
        Assert.Equal(30, loaded.ReportRetentionDays);
        Assert.True(loaded.ReportingEnabled);
        Assert.True(loaded.SuspectFreezing);
        Assert.Equal(DefaultCodes, loaded.GetReasons().Select(r => r.Code));
    }

    [Fact]
    public void EmptyReasonList_UsesTheDefaults()
    {
        var loaded = RoundTrip(Save(new PluginConfiguration { Reasons = [] }));

        Assert.Equal(DefaultCodes, loaded.GetReasons().Select(r => r.Code));
    }

    [Fact]
    public void ReasonsAreReturnedInTheirOrder()
    {
        var config = new PluginConfiguration
        {
            Reasons =
            [
                new ReasonEntry { Code = "b", Label = "B", Order = 2 },
                new ReasonEntry { Code = "a", Label = "A", Order = 1 },
            ],
        };

        Assert.Equal(["a", "b"], config.GetReasons().Select(r => r.Code));
    }

    private static string Save(PluginConfiguration config)
    {
        using var writer = new StringWriter();
        new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, config);
        return writer.ToString();
    }

    private static PluginConfiguration RoundTrip(string xml)
    {
        using var reader = new StringReader(xml.Trim());
        return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
    }
}
