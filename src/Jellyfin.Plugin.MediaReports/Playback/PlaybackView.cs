using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaReports.Playback;

/// <summary>
/// One of the caller's playbacks as <c>GET /MediaReports/Playbacks</c> answers it. A public
/// model, apart from the capture record, so the record can change without changing the API.
/// </summary>
public sealed record PlaybackView
{
    /// <summary>Gets where the playback stands: Playing, Stopped or Ended.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>Gets when the server last saw it.</summary>
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>Gets the app's name.</summary>
    public string? Client { get; init; }

    /// <summary>Gets the app's version.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>Gets the device's name.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Gets the item played.</summary>
    public Guid? ItemId { get; init; }

    /// <summary>Gets the item's name.</summary>
    public string? ItemName { get; init; }

    /// <summary>Gets the play method: DirectPlay, DirectStream or Transcode.</summary>
    public string? PlayMethod { get; init; }

    /// <summary>Gets the position, in ticks of 100 ns.</summary>
    public long? PositionTicks { get; init; }

    /// <summary>Gets the audio stream in use.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets the subtitle stream in use.</summary>
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>Gets the transcode, or null.</summary>
    public PlaybackTranscode? Transcoding { get; init; }

    /// <summary>Gets the source file's streams.</summary>
    public IReadOnlyList<PlaybackSourceStream> SourceStreams { get; init; } = [];

    /// <summary>Gets the source file's bitrate.</summary>
    public int? SourceBitrate { get; init; }

    /// <summary>
    /// Gets the view of a captured playback.
    /// </summary>
    /// <param name="entry">The captured playback.</param>
    /// <returns>The view.</returns>
    public static PlaybackView From(PlaybackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new PlaybackView
        {
            State = entry.State.ToString(),
            LastSeen = entry.LastSeen,
            Client = entry.Client,
            ClientVersion = entry.ClientVersion,
            DeviceName = entry.DeviceName,
            ItemId = entry.ItemId,
            ItemName = entry.ItemName,
            PlayMethod = entry.PlayMethod,
            PositionTicks = entry.PositionTicks,
            AudioStreamIndex = entry.AudioStreamIndex,
            SubtitleStreamIndex = entry.SubtitleStreamIndex,
            Transcoding = entry.Transcoding is { } t ? new PlaybackTranscode
            {
                VideoCodec = t.VideoCodec,
                AudioCodec = t.AudioCodec,
                Container = t.Container,
                IsVideoDirect = t.IsVideoDirect,
                IsAudioDirect = t.IsAudioDirect,
                Bitrate = t.Bitrate,
                Width = t.Width,
                Height = t.Height,
                HardwareAccelerationType = t.HardwareAccelerationType,
                TranscodeReasons = t.TranscodeReasons,
            } : null,
            SourceStreams = entry.SourceStreams.Select(s => new PlaybackSourceStream
            {
                Index = s.Index,
                Type = s.Type,
                Codec = s.Codec,
                Language = s.Language,
                Bitrate = s.BitRate,
                Width = s.Width,
                Height = s.Height,
                Channels = s.Channels,
                FrameRate = s.FrameRate,
            }).ToList(),
            SourceBitrate = entry.SourceBitrate,
        };
    }
}

/// <summary>
/// A playback's transcode, as the playbacks route answers it.
/// </summary>
public sealed record PlaybackTranscode
{
    /// <summary>Gets the output video codec.</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Gets the output audio codec.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Gets the output container.</summary>
    public string? Container { get; init; }

    /// <summary>Gets a value indicating whether video is copied.</summary>
    public bool? IsVideoDirect { get; init; }

    /// <summary>Gets a value indicating whether audio is copied.</summary>
    public bool? IsAudioDirect { get; init; }

    /// <summary>Gets the output bitrate.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Gets the output width.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the output height.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the hardware acceleration type, with Jellyfin's own name.</summary>
    public string? HardwareAccelerationType { get; init; }

    /// <summary>Gets why the server transcoded, with Jellyfin's own names.</summary>
    public IReadOnlyList<string> TranscodeReasons { get; init; } = [];
}

/// <summary>
/// One stream of a playback's source file, as the playbacks route answers it.
/// </summary>
public sealed record PlaybackSourceStream
{
    /// <summary>Gets the stream index.</summary>
    public int Index { get; init; }

    /// <summary>Gets the stream type: Video, Audio or Subtitle.</summary>
    public string? Type { get; init; }

    /// <summary>Gets the codec.</summary>
    public string? Codec { get; init; }

    /// <summary>Gets the language.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the bitrate.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Gets the width.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the height.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the channel count.</summary>
    public int? Channels { get; init; }

    /// <summary>Gets the frame rate, for a video stream.</summary>
    public float? FrameRate { get; init; }
}
