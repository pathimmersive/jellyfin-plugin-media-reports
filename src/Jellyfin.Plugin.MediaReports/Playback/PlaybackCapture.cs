using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.MediaReports.Playback;

/// <summary>
/// Keeps the last state of each recent playback session, so its diagnostics are still
/// known after the client stops or the session ends. The server itself keeps no item,
/// play method or transcoding details once playback stops.
/// </summary>
public sealed class PlaybackCapture : IHostedService
{
    /// <summary>
    /// The most playbacks kept and returned per user.
    /// </summary>
    public const int MaxPerUser = 10;

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _retention;
    private readonly Dictionary<string, PlaybackEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCapture"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    public PlaybackCapture(ISessionManager sessionManager, ILibraryManager libraryManager)
        : this(sessionManager, libraryManager, TimeProvider.System, ConfiguredRetention)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCapture"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="time">The clock.</param>
    /// <param name="retention">Returns the current retention window; read on every use.</param>
    public PlaybackCapture(ISessionManager sessionManager, ILibraryManager libraryManager, TimeProvider time, Func<TimeSpan> retention)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _time = time;
        _retention = retention;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackProgress;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.SessionEnded += OnSessionEnded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackProgress;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionEnded -= OnSessionEnded;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets a user's live and recent playbacks, newest first.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>At most <see cref="MaxPerUser"/> playbacks.</returns>
    public IReadOnlyList<PlaybackEntry> GetPlaybacks(Guid userId)
    {
        var live = _sessionManager.Sessions
            .Where(s => s.UserId.Equals(userId) && s.NowPlayingItem is not null)
            .ToList();

        lock (_lock)
        {
            Prune();
            var result = new List<PlaybackEntry>();
            foreach (var session in live)
            {
                _entries.TryGetValue(session.Id, out var previous);
                result.Add(Observe(session, previous));
            }

            var liveIds = live.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            result.AddRange(_entries.Values.Where(e => e.UserId.Equals(userId) && !liveIds.Contains(e.SessionId)));
            return result.OrderByDescending(e => e.LastSeen).Take(MaxPerUser).ToList();
        }
    }

    private static TimeSpan ConfiguredRetention()
        => TimeSpan.FromHours(Math.Max(0, Plugin.Instance?.Configuration.PlaybackRetentionHours ?? 24));

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Session is not { } session || session.NowPlayingItem is null)
        {
            return;
        }

        lock (_lock)
        {
            _entries.TryGetValue(session.Id, out var previous);
            _entries[session.Id] = Observe(session, previous);
            Prune();
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session is not { } session)
        {
            return;
        }

        lock (_lock)
        {
            // The stop event's session has lost the item, play method and transcoding,
            // so it never replaces what an earlier event recorded.
            var entry = _entries.TryGetValue(session.Id, out var previous)
                ? previous
                : Observe(session, null) with { ItemId = e.Item?.Id, ItemName = e.Item?.Name, MediaSourceId = e.MediaSourceId };
            _entries[session.Id] = entry with
            {
                State = PlaybackState.Stopped,
                PositionTicks = e.PlaybackPositionTicks ?? entry.PositionTicks,
                LastSeen = _time.GetUtcNow(),
            };
            Prune();
        }
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        if (e.SessionInfo is not { } session)
        {
            return;
        }

        lock (_lock)
        {
            if (_entries.TryGetValue(session.Id, out var previous) && previous.State == PlaybackState.Playing)
            {
                _entries[session.Id] = previous with { State = PlaybackState.Ended, LastSeen = _time.GetUtcNow() };
            }
        }
    }

    // Drops entries past the retention window and all but the newest per user.
    private void Prune()
    {
        var cutoff = _time.GetUtcNow() - _retention();
        foreach (var expired in _entries.Values.Where(e => e.LastSeen <= cutoff).ToList())
        {
            _entries.Remove(expired.SessionId);
        }

        foreach (var user in _entries.Values.GroupBy(e => e.UserId))
        {
            foreach (var excess in user.OrderByDescending(e => e.LastSeen).Skip(MaxPerUser).ToList())
            {
                _entries.Remove(excess.SessionId);
            }
        }
    }

    private PlaybackEntry Observe(SessionInfo session, PlaybackEntry? previous)
    {
        var playState = session.PlayState;
        var itemId = session.NowPlayingItem?.Id;
        var mediaSourceId = playState?.MediaSourceId;
        var playMethod = playState?.PlayMethod?.ToString();
        var sameSource = previous is not null
            && previous.ItemId == itemId
            && previous.MediaSourceId == mediaSourceId;

        TranscodingDetails? transcoding = null;
        if (playMethod == "Transcode")
        {
            transcoding = session.TranscodingInfo is { } info
                ? Transcoding(info)
                : sameSource && previous!.PlayMethod == "Transcode" ? previous.Transcoding : null;
        }

        return new PlaybackEntry
        {
            SessionId = session.Id,
            State = PlaybackState.Playing,
            LastSeen = _time.GetUtcNow(),
            UserId = session.UserId,
            Client = session.Client,
            ClientVersion = session.ApplicationVersion,
            DeviceName = session.DeviceName,
            DeviceId = session.DeviceId,
            ItemId = itemId,
            ItemName = session.NowPlayingItem?.Name,
            MediaSourceId = mediaSourceId,
            PlayMethod = playMethod,
            PositionTicks = playState?.PositionTicks,
            AudioStreamIndex = playState?.AudioStreamIndex,
            SubtitleStreamIndex = playState?.SubtitleStreamIndex is >= 0 ? playState.SubtitleStreamIndex : null,
            Transcoding = transcoding,
            SourceStreams = sameSource ? previous!.SourceStreams : SourceStreams(session, itemId, mediaSourceId),
        };
    }

    private static TranscodingDetails Transcoding(MediaBrowser.Model.Session.TranscodingInfo info) => new()
    {
        VideoCodec = info.VideoCodec,
        AudioCodec = info.AudioCodec,
        Container = info.Container,
        IsVideoDirect = info.IsVideoDirect,
        IsAudioDirect = info.IsAudioDirect,
        Bitrate = info.Bitrate,
        Width = info.Width,
        Height = info.Height,
        HardwareAccelerationType = info.HardwareAccelerationType?.ToString(),
        TranscodeReasons = info.TranscodeReasons.ToString()
            .Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Where(r => r != "0")
            .ToList(),
    };

    private IReadOnlyList<SourceStream> SourceStreams(SessionInfo session, Guid? itemId, string? mediaSourceId)
    {
        var item = session.FullNowPlayingItem ?? (itemId is { } id ? _libraryManager.GetItemById(id) : null);
        if (item is null)
        {
            return [];
        }

        var source = Guid.TryParse(mediaSourceId, out var sourceId) && !sourceId.Equals(item.Id)
            ? _libraryManager.GetItemById(sourceId) ?? item
            : item;
        return source.GetMediaStreams()
            .Select(s => new SourceStream
            {
                Index = s.Index,
                Type = s.Type.ToString(),
                Codec = s.Codec,
                Language = s.Language,
                BitRate = s.BitRate,
                Width = s.Width,
                Height = s.Height,
                Channels = s.Channels,
            })
            .ToList();
    }
}
