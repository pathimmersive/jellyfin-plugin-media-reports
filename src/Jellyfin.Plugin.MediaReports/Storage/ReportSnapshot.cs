using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaReports.Playback;

namespace Jellyfin.Plugin.MediaReports.Storage;

/// <summary>
/// The playback diagnostics stored with a report. It is built from what the server saw of
/// the playback and holds no IP address, no file path and no user id.
/// </summary>
public sealed record ReportSnapshot
{
    /// <summary>
    /// The shape version written by this build. Older shapes stay readable.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the shape version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets where the playback stood: Playing, Stopped or Ended.</summary>
    public string? State { get; init; }

    /// <summary>Gets when the server last saw the playback.</summary>
    public DateTimeOffset? Captured { get; init; }

    /// <summary>Gets the client app name.</summary>
    public string? Client { get; init; }

    /// <summary>Gets the client app version.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>Gets the device name.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Gets the media source id.</summary>
    public string? MediaSourceId { get; init; }

    /// <summary>Gets the play method.</summary>
    public string? PlayMethod { get; init; }

    /// <summary>Gets the position in ticks.</summary>
    public long? PositionTicks { get; init; }

    /// <summary>Gets the selected audio stream index.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets the selected subtitle stream index.</summary>
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>Gets the transcoding details, present only for a transcode.</summary>
    public TranscodingDetails? Transcoding { get; init; }

    /// <summary>Gets the source file's media streams.</summary>
    public IReadOnlyList<SourceStream> SourceStreams { get; init; } = [];

    /// <summary>
    /// Builds a snapshot from a captured playback.
    /// </summary>
    /// <param name="playback">The playback.</param>
    /// <returns>The snapshot.</returns>
    public static ReportSnapshot FromPlayback(PlaybackEntry playback) => new()
    {
        State = playback.State.ToString(),
        Captured = playback.LastSeen == default ? null : playback.LastSeen,
        Client = playback.Client,
        ClientVersion = playback.ClientVersion,
        DeviceName = playback.DeviceName,
        MediaSourceId = playback.MediaSourceId,
        PlayMethod = playback.PlayMethod,
        PositionTicks = playback.PositionTicks,
        AudioStreamIndex = playback.AudioStreamIndex,
        SubtitleStreamIndex = playback.SubtitleStreamIndex,
        Transcoding = playback.Transcoding,
        SourceStreams = playback.SourceStreams,
    };
}
