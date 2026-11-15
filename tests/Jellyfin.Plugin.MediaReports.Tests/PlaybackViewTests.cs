using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediaReports.Playback;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class PlaybackViewTests
{
    private static readonly PlaybackEntry Entry = new()
    {
        SessionId = "session-1",
        State = PlaybackState.Stopped,
        LastSeen = DateTimeOffset.UnixEpoch,
        UserId = Guid.NewGuid(),
        Client = "Jellyfin Web",
        ClientVersion = "12.0.0",
        DeviceName = "Living Room TV",
        DeviceId = "device-1",
        ItemId = Guid.NewGuid(),
        ItemName = "Test Movie",
        MediaSourceId = "source-1",
        PlayMethod = "Transcode",
        PositionTicks = 600_000_000,
        AudioStreamIndex = 1,
        SubtitleStreamIndex = 2,
        Transcoding = new TranscodingDetails { VideoCodec = "h264", AudioCodec = "aac", Bitrate = 1_000_000, TranscodeReasons = ["ContainerBitrateExceedsLimit"] },
        SourceStreams = [new SourceStream { Index = 0, Type = "Video", Codec = "hevc", BitRate = 20_000_000, Width = 3840, Height = 2160, FrameRate = 24 }],
        SourceBitrate = 20_000_000,
    };

    [Fact]
    public void Playbacks_AnswerTheDocumentedFields_AndNoIds()
    {
        var json = JsonSerializer.SerializeToElement(PlaybacksController.Shape(Entry));
        var documented = DocumentedFields();

        Assert.Equal(documented["Playback"], Names(json));
        Assert.Equal(documented["Transcoding"], Names(json.GetProperty("Transcoding")));
        Assert.Equal(documented["SourceStreams"], Names(json.GetProperty("SourceStreams")[0]));
        Assert.Equal("Stopped", json.GetProperty("State").GetString());
        Assert.Equal(20_000_000, json.GetProperty("SourceStreams")[0].GetProperty("Bitrate").GetInt32());
        foreach (var hidden in new[] { "SessionId", "UserId", "DeviceId", "MediaSourceId" })
        {
            Assert.False(json.TryGetProperty(hidden, out _), hidden + " is in the answer");
        }
    }

    private static List<string> Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

    // The field tables under the playbacks route in docs/api.md, one per "**Name**" heading line.
    private static Dictionary<string, List<string>> DocumentedFields()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "api.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "api.md"));
        var start = text.IndexOf("### `GET /MediaReports/Playbacks`", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/api.md has no playbacks section");
        var end = text.IndexOf("\n### ", start + 5, StringComparison.Ordinal);
        var section = text[start..end];

        var tables = new Dictionary<string, List<string>>();
        string? current = null;
        foreach (var line in section.Split('\n'))
        {
            var heading = Regex.Match(line, @"^Fields of `(\w+)`:");
            if (heading.Success)
            {
                current = heading.Groups[1].Value;
                tables[current] = [];
                continue;
            }

            var row = Regex.Match(line, @"^\| `(\w+)` \|");
            if (current is not null && row.Success)
            {
                tables[current].Add(row.Groups[1].Value);
            }
        }

        foreach (var name in new[] { "Playback", "Transcoding", "SourceStreams" })
        {
            Assert.True(tables.ContainsKey(name), $"docs/api.md has no field table for {name}");
            tables[name].Sort(StringComparer.Ordinal);
        }

        return tables;
    }
}
