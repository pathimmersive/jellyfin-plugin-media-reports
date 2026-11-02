using System;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaReports.Configuration;

/// <summary>
/// The plugin's saved configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether users can report problems. Off removes the
    /// Report button and refuses new reports.
    /// </summary>
    public bool ReportingEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the reasons users pick from. Empty means the default reasons.
    /// </summary>
    /// <remarks>An array, so loading a saved file replaces the defaults instead of adding to them.</remarks>
    public ReasonEntry[] Reasons { get; set; } = DefaultReasons();

    /// <summary>
    /// Gets or sets a value indicating whether freezing playback is listed as a suspected problem.
    /// </summary>
    public bool SuspectFreezing { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a transcoder slower than the video is listed as a suspected problem.
    /// </summary>
    public bool SuspectSlowTranscode { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether playback sent far below the file's quality is listed as a suspected problem.
    /// </summary>
    public bool SuspectLowQuality { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a direct play that fell back to transcoding is listed as a suspected problem.
    /// </summary>
    public bool SuspectPlaybackError { get; set; } = true;

    /// <summary>
    /// Gets or sets the Discord webhook address; empty sends nothing.
    /// </summary>
    public string DiscordWebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the generic JSON webhook address; empty sends nothing.
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many hours a playback is kept after it was last seen, so a problem
    /// can still be reported after playback stops. Zero keeps nothing after a stop.
    /// </summary>
    public int PlaybackRetentionHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets how many days a closed report is kept after it closed. Zero, or a
    /// negative value, keeps closed reports forever. Open reports are never deleted.
    /// </summary>
    public int ReportRetentionDays { get; set; } = 365;

    /// <summary>
    /// Gets the default reasons, in order.
    /// </summary>
    /// <returns>A new array of the eight default reasons.</returns>
    public static ReasonEntry[] DefaultReasons() =>
    [
        new() { Code = "wont-play", Label = "Won't play", Order = 0 },
        new() { Code = "playback-error", Label = "Playback error", Order = 1 },
        new() { Code = "poor-quality", Label = "Poor quality", Order = 2 },
        new() { Code = "stutter", Label = "Stutter or jitter", Order = 3 },
        new() { Code = "audio", Label = "Audio problem", Order = 4 },
        new() { Code = "subtitles", Label = "Subtitle problem", Order = 5 },
        new() { Code = "metadata", Label = "Wrong metadata", Order = 6 },
        new() { Code = "other", Label = "Other", Order = 7 },
    ];

    /// <summary>
    /// Gets every reason, retired ones included, in order; the defaults when none are saved.
    /// </summary>
    /// <returns>The reasons.</returns>
    public ReasonEntry[] GetReasons()
    {
        var reasons = Reasons is { Length: > 0 } saved ? saved : DefaultReasons();
        return reasons.OrderBy(r => r.Order).ToArray();
    }
}

/// <summary>
/// One reason users can pick. The code never changes, so reports keep their reason when it is renamed.
/// </summary>
public sealed record ReasonEntry
{
    /// <summary>Gets or sets the stable code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the label users see.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the position in the list.</summary>
    public int Order { get; set; }

    /// <summary>Gets or sets a value indicating whether the reason is no longer offered.</summary>
    public bool Retired { get; set; }
}
