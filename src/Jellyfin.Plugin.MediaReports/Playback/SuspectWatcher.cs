using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Playback;

/// <summary>
/// What one progress event of a playback showed the server.
/// </summary>
public sealed record SuspectSample
{
    /// <summary>Gets when it was seen.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Gets the session.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the play session: the stream the client asked for.</summary>
    public string? PlaySessionId { get; init; }

    /// <summary>Gets the user.</summary>
    public Guid UserId { get; init; }

    /// <summary>Gets the user's name.</summary>
    public string? UserName { get; init; }

    /// <summary>Gets the client name.</summary>
    public string? Client { get; init; }

    /// <summary>Gets the device name.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Gets the item playing.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the reported position.</summary>
    public long? PositionTicks { get; init; }

    /// <summary>Gets a value indicating whether playback is paused.</summary>
    public bool IsPaused { get; init; }

    /// <summary>Gets the play method: DirectPlay, DirectStream or Transcode.</summary>
    public string? PlayMethod { get; init; }

    /// <summary>Gets the transcode frame rate.</summary>
    public float? TranscodeFps { get; init; }

    /// <summary>Gets how far the transcode has got, in percent.</summary>
    public double? Completion { get; init; }

    /// <summary>Gets the transcode's output bitrate.</summary>
    public int? OutputBitrate { get; init; }

    /// <summary>Gets the transcode's output height.</summary>
    public int? OutputHeight { get; init; }

    /// <summary>Gets the transcode reasons.</summary>
    public IReadOnlyList<string> TranscodeReasons { get; init; } = [];

    /// <summary>Gets the source's frame rate.</summary>
    public float? SourceFps { get; init; }

    /// <summary>Gets the source's bitrate.</summary>
    public int? SourceBitrate { get; init; }

    /// <summary>Gets the source's height.</summary>
    public int? SourceHeight { get; init; }
}

/// <summary>
/// The title a finding is about, as the library names it.
/// </summary>
/// <param name="ItemType">Movie, Episode or another type.</param>
/// <param name="Title">The title.</param>
/// <param name="SeriesId">The series, for an episode.</param>
/// <param name="SeriesName">The series name.</param>
/// <param name="Season">The season number.</param>
/// <param name="Episode">The episode number.</param>
public sealed record FindingTitle(string? ItemType, string Title, Guid? SeriesId, string? SeriesName, int? Season, int? Episode);

/// <summary>
/// Watches every playback for the four kinds of suspected problem and keeps one finding per
/// playback and kind. Findings never become reports or issues and never reach the viewer.
/// </summary>
public sealed class SuspectWatcher
{
    /// <summary>Freezing: the reported position keeps jumping back.</summary>
    public const string Freezing = "freezing";

    /// <summary>Transcoder too slow: the transcode can't keep up with the source.</summary>
    public const string SlowTranscode = "slow-transcode";

    /// <summary>Low quality: a bitrate limit sends far less than the source.</summary>
    public const string LowQuality = "low-quality";

    /// <summary>Playback error mid-play: a switch to transcoding after a direct play error.</summary>
    public const string PlaybackError = "playback-error";

    /// <summary>How far back the position must go to count as a jump.</summary>
    public static readonly TimeSpan JumpBack = TimeSpan.FromSeconds(5);

    /// <summary>How many jumps make a Freezing finding.</summary>
    public const int Jumps = 3;

    /// <summary>The window the jumps must fall in.</summary>
    public static readonly TimeSpan JumpWindow = TimeSpan.FromMinutes(10);

    /// <summary>The start of a playback that is ignored.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    /// <summary>How long the transcode must stay slow.</summary>
    public static readonly TimeSpan SlowFor = TimeSpan.FromSeconds(30);

    /// <summary>The share of the source frame rate under which a transcode is slow.</summary>
    public const double SlowShare = 0.9;

    private static readonly string[] BitrateLimits = ["ContainerBitrateExceedsLimit", "VideoBitrateNotSupported"];

    private readonly ReportStore _store;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<Guid, FindingTitle?> _title;
    private readonly Action<string, string?> _added;
    private readonly ILogger<SuspectWatcher> _logger;
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="SuspectWatcher"/> class.
    /// </summary>
    /// <param name="store">The report store, where findings are kept.</param>
    /// <param name="config">Returns the current configuration, for the switches.</param>
    /// <param name="title">Names the title of an item.</param>
    /// <param name="added">Called with the kind and client of each new finding.</param>
    /// <param name="logger">The logger.</param>
    public SuspectWatcher(ReportStore store, Func<PluginConfiguration> config, Func<Guid, FindingTitle?> title, Action<string, string?> added, ILogger<SuspectWatcher> logger)
    {
        _store = store;
        _config = config;
        _title = title;
        _added = added;
        _logger = logger;
    }

    /// <summary>
    /// Gets a task that completes when the samples queued so far have been looked at.
    /// </summary>
    public Task WhenIdle
    {
        get
        {
            lock (_lock)
            {
                return _tail;
            }
        }
    }

    /// <summary>
    /// Queues a sample to be looked at off the caller's thread. A failure is logged and never
    /// reaches the caller.
    /// </summary>
    /// <param name="sample">The sample.</param>
    public void Queue(SuspectSample sample)
    {
        lock (_lock)
        {
            _tail = _tail.ContinueWith(
                _ =>
                {
                    try
                    {
                        Observe(sample);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Couldn't check a playback sample for suspected problems");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Looks at one sample now and adds or updates findings.
    /// </summary>
    /// <param name="sample">The sample.</param>
    public void Observe(SuspectSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var key = sample.SessionId + "/" + sample.ItemId.ToString("N", CultureInfo.InvariantCulture);
        var config = _config();
        Watch watch;
        lock (_watches)
        {
            PruneWatches(sample.At);
            if (!_watches.TryGetValue(key, out watch!))
            {
                watch = new Watch(sample.At);
                _watches[key] = watch;
            }
        }

        lock (watch)
        {
            if (config.SuspectFreezing && CheckFreezing(watch, sample) is { } freezing)
            {
                Record(Freezing, key, sample, freezing);
            }

            if (config.SuspectSlowTranscode && CheckSlowTranscode(watch, sample) is { } slow)
            {
                Record(SlowTranscode, key, sample, slow);
            }

            if (config.SuspectLowQuality && CheckLowQuality(sample) is { } low)
            {
                Record(LowQuality, key, sample, low);
            }

            if (config.SuspectPlaybackError && CheckPlaybackError(watch, sample) is { } error)
            {
                Record(PlaybackError, key, sample, error);
            }

            watch.LastPosition = sample.PositionTicks;
            watch.LastPaused = sample.IsPaused;
            watch.LastPlayMethod = sample.PlayMethod ?? watch.LastPlayMethod;
            watch.LastPlaySessionId = sample.PlaySessionId ?? watch.LastPlaySessionId;
            watch.LastSeen = sample.At;
        }
    }

    /// <summary>
    /// Describes a bitrate in words, as "1.1 Mbps".
    /// </summary>
    /// <param name="bitsPerSecond">The bitrate.</param>
    /// <returns>The words.</returns>
    public static string Mbps(int bitsPerSecond) =>
        (bitsPerSecond / 1_000_000d).ToString(bitsPerSecond >= 10_000_000 ? "0" : "0.0", CultureInfo.InvariantCulture) + " Mbps";

    private static string? CheckFreezing(Watch watch, SuspectSample sample)
    {
        if (sample.IsPaused || watch.LastPaused || sample.PositionTicks is not { } position || watch.LastPosition is not { } last
            || sample.At - watch.Started < Settle)
        {
            return null;
        }

        var back = TimeSpan.FromTicks(last - position);
        if (back < JumpBack)
        {
            return null;
        }

        watch.Jumps.Add((sample.At, back));
        watch.Jumps.RemoveAll(j => sample.At - j.At > JumpWindow);
        if (watch.Jumps.Count < Jumps)
        {
            return null;
        }

        var seconds = Math.Round(watch.Jumps.Average(j => j.Back.TotalSeconds));
        var minutes = Math.Max(1, (int)Math.Ceiling((sample.At - watch.Jumps[0].At).TotalMinutes));
        return string.Create(CultureInfo.InvariantCulture, $"Position jumped back {seconds:0} s, {watch.Jumps.Count} times in {minutes} {(minutes == 1 ? "minute" : "minutes")}");
    }

    private static string? CheckSlowTranscode(Watch watch, SuspectSample sample)
    {
        if (sample.PlayMethod != "Transcode" || sample.TranscodeFps is not { } fps || sample.Completion is not { } completion
            || completion >= 100 || sample.SourceFps is not { } source || source <= 0)
        {
            return null;
        }

        if (fps >= source * SlowShare)
        {
            watch.SlowSince = null;
            watch.SlowFps.Clear();
            return null;
        }

        watch.SlowSince ??= sample.At;
        watch.SlowFps.Add(fps);
        var slowFor = sample.At - watch.SlowSince.Value;
        if (slowFor < SlowFor)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"Transcoding at {watch.SlowFps.Average():0.0} fps against {source:0.##} fps for {slowFor.TotalSeconds:0} s");
    }

    private static string? CheckLowQuality(SuspectSample sample)
    {
        if (sample.PlayMethod != "Transcode" || !sample.TranscodeReasons.Any(r => BitrateLimits.Contains(r, StringComparer.Ordinal)))
        {
            return null;
        }

        var lowBitrate = sample.OutputBitrate is { } output && sample.SourceBitrate is { } source && output < source / 4;
        var lowHeight = sample.OutputHeight is { } height && sample.SourceHeight is { } sourceHeight && height < sourceHeight;
        if (!lowBitrate && !lowHeight)
        {
            return null;
        }

        var sent = Describe(sample.OutputBitrate, sample.OutputHeight);
        var from = Describe(sample.SourceBitrate, sample.SourceHeight);
        return $"Sent at {sent} from {from} (bitrate limit)";
    }

    // Jellyfin reports a transcode as Direct play until its transcoding job starts, so only a
    // switch under a new play session (the client asked for a new stream) counts.
    private static string? CheckPlaybackError(Watch watch, SuspectSample sample) =>
        watch.LastPlayMethod is "DirectPlay" or "DirectStream"
        && sample.PlayMethod == "Transcode"
        && sample.TranscodeReasons.Contains("DirectPlayError", StringComparer.Ordinal)
        && watch.LastPlaySessionId is { } before && sample.PlaySessionId is { } after
        && !string.Equals(before, after, StringComparison.Ordinal)
            ? "Switched to transcoding: direct play error"
            : null;

    private static string Describe(int? bitrate, int? height) =>
        string.Join(' ', new[] { bitrate is { } b ? Mbps(b) : null, height is { } h ? h.ToString(CultureInfo.InvariantCulture) + "p" : null }.Where(p => p is not null));

    private void Record(string kind, string playbackKey, SuspectSample sample, string detail)
    {
        var title = _title(sample.ItemId) ?? new FindingTitle(null, "Unknown title", null, null, null, null);
        var added = _store.SaveFinding(new NewFinding
        {
            Kind = kind,
            PlaybackKey = playbackKey,
            ItemId = sample.ItemId,
            ItemType = title.ItemType,
            Title = title.Title,
            SeriesId = title.SeriesId,
            SeriesName = title.SeriesName,
            SeasonNumber = title.Season,
            EpisodeNumber = title.Episode,
            UserId = sample.UserId,
            UserName = sample.UserName,
            Device = sample.DeviceName,
            Client = sample.Client,
            Detail = detail,
        });
        if (added)
        {
            _logger.LogInformation("Suspected problem ({Kind}) on {Title} in {Client}: {Detail}", kind, title.Title, sample.Client, detail);
            _added(kind, sample.Client);
        }
    }

    // A playback not seen for an hour is over.
    private void PruneWatches(DateTimeOffset now)
    {
        foreach (var stale in _watches.Where(w => now - w.Value.LastSeen > TimeSpan.FromHours(1)).Select(w => w.Key).ToList())
        {
            _watches.Remove(stale);
        }
    }

    private sealed class Watch(DateTimeOffset started)
    {
        public DateTimeOffset Started { get; } = started;

        public DateTimeOffset LastSeen { get; set; } = started;

        public long? LastPosition { get; set; }

        public bool LastPaused { get; set; }

        public string? LastPlayMethod { get; set; }

        public string? LastPlaySessionId { get; set; }

        public List<(DateTimeOffset At, TimeSpan Back)> Jumps { get; } = [];

        public DateTimeOffset? SlowSince { get; set; }

        public List<float> SlowFps { get; } = [];
    }
}
