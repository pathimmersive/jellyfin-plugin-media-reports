using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaReports.Playback;

/// <summary>
/// Where a playback stands.
/// </summary>
public enum PlaybackState
{
    /// <summary>The session is playing it.</summary>
    Playing,

    /// <summary>The client reported a stop.</summary>
    Stopped,

    /// <summary>The session ended without a stop.</summary>
    Ended,
}

/// <summary>
/// The last state the server saw for one playback session.
/// </summary>
public sealed record PlaybackEntry
{
    /// <summary>Gets the playback session id.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets where the playback stands.</summary>
    public PlaybackState State { get; init; }

    /// <summary>Gets when the server last saw it.</summary>
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>Gets the user id.</summary>
    public Guid UserId { get; init; }

    /// <summary>Gets the client app name.</summary>
    public string? Client { get; init; }

    /// <summary>Gets the client app version.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>Gets the device name.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Gets the device id.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Gets the item id.</summary>
    public Guid? ItemId { get; init; }

    /// <summary>Gets the item name.</summary>
    public string? ItemName { get; init; }

    /// <summary>Gets the media source id.</summary>
    public string? MediaSourceId { get; init; }

    /// <summary>Gets the play method: DirectPlay, DirectStream or Transcode.</summary>
    public string? PlayMethod { get; init; }

    /// <summary>Gets the playback position in ticks.</summary>
    public long? PositionTicks { get; init; }

    /// <summary>Gets the selected audio stream index.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets the selected subtitle stream index, absent when none is selected.</summary>
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>Gets the transcoding details, present only when the play method is Transcode.</summary>
    public TranscodingDetails? Transcoding { get; init; }

    /// <summary>Gets the item's source media streams.</summary>
    public IReadOnlyList<SourceStream> SourceStreams { get; init; } = [];
}

/// <summary>
/// What the server's transcoder produced and why.
/// </summary>
public sealed record TranscodingDetails
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
/// One media stream of the source file.
/// </summary>
public sealed record SourceStream
{
    /// <summary>Gets the stream index.</summary>
    public int Index { get; init; }

    /// <summary>Gets the stream type.</summary>
    public string? Type { get; init; }

    /// <summary>Gets the codec.</summary>
    public string? Codec { get; init; }

    /// <summary>Gets the language.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the bitrate.</summary>
    public int? BitRate { get; init; }

    /// <summary>Gets the width.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the height.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the channel count.</summary>
    public int? Channels { get; init; }
}
