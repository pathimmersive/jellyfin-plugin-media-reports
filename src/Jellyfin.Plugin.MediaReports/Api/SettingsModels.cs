using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// The settings the Settings tab shows and saves.
/// </summary>
public sealed record AdminSettings
{
    /// <summary>Gets a value indicating whether users can report problems.</summary>
    public bool ReportingEnabled { get; init; }

    /// <summary>Gets every reason, retired ones included, in order, with how many reports use each.</summary>
    public IReadOnlyList<SettingsReason> Reasons { get; init; } = [];

    /// <summary>Gets how many days a closed report is kept; 0 keeps them forever.</summary>
    public int ReportRetentionDays { get; init; }

    /// <summary>Gets a value indicating whether freezing playback is a suspected problem.</summary>
    public bool SuspectFreezing { get; init; }

    /// <summary>Gets a value indicating whether a transcoder slower than the video is a suspected problem.</summary>
    public bool SuspectSlowTranscode { get; init; }

    /// <summary>Gets a value indicating whether playback far below the file's quality is a suspected problem.</summary>
    public bool SuspectLowQuality { get; init; }

    /// <summary>Gets a value indicating whether a direct play that fell back to transcoding is a suspected problem.</summary>
    public bool SuspectPlaybackError { get; init; }

    /// <summary>Gets the Discord webhook address; empty sends nothing.</summary>
    public string DiscordWebhookUrl { get; init; } = string.Empty;

    /// <summary>Gets the generic JSON webhook address; empty sends nothing.</summary>
    public string WebhookUrl { get; init; } = string.Empty;

    /// <summary>Gets the address Prometheus scrapes the plugin's metrics from.</summary>
    public string MetricsUrl { get; init; } = string.Empty;
}

/// <summary>
/// One reason as the Settings tab shows it.
/// </summary>
/// <param name="Code">The stable code.</param>
/// <param name="Label">The label users see.</param>
/// <param name="Retired">Whether it is no longer offered.</param>
/// <param name="Reports">How many reports use it.</param>
public sealed record SettingsReason(string Code, string Label, bool Retired, int Reports);

/// <summary>
/// A save from the Settings tab. A field left out keeps its saved value.
/// </summary>
public sealed record SettingsUpdate
{
    /// <summary>Gets whether users can report problems.</summary>
    public bool? ReportingEnabled { get; init; }

    /// <summary>
    /// Gets the reasons in their new order. A reason without a code is new and gets one;
    /// a saved reason left out is kept, since reasons are never deleted.
    /// </summary>
    public IReadOnlyList<ReasonUpdate>? Reasons { get; init; }

    /// <summary>Gets the days to keep closed reports, as a number or the text typed.</summary>
    public JsonElement? ReportRetentionDays { get; init; }

    /// <summary>Gets whether freezing playback is a suspected problem.</summary>
    public bool? SuspectFreezing { get; init; }

    /// <summary>Gets whether a slow transcoder is a suspected problem.</summary>
    public bool? SuspectSlowTranscode { get; init; }

    /// <summary>Gets whether low-quality playback is a suspected problem.</summary>
    public bool? SuspectLowQuality { get; init; }

    /// <summary>Gets whether a fallback to transcoding is a suspected problem.</summary>
    public bool? SuspectPlaybackError { get; init; }

    /// <summary>Gets the Discord webhook address.</summary>
    public string? DiscordWebhookUrl { get; init; }

    /// <summary>Gets the generic JSON webhook address.</summary>
    public string? WebhookUrl { get; init; }
}

/// <summary>
/// One reason in a save.
/// </summary>
/// <param name="Code">The reason's code, or null for a new reason.</param>
/// <param name="Label">The label users see.</param>
/// <param name="Retired">Whether it is no longer offered.</param>
public sealed record ReasonUpdate(string? Code, string? Label, bool Retired);

/// <summary>
/// A refused save: what is wrong and the field at fault.
/// </summary>
/// <param name="Code">The error code.</param>
/// <param name="Message">What to fix, shown beside the field.</param>
/// <param name="Field">The field at fault: reasons, reportRetentionDays, discordWebhookUrl or webhookUrl.</param>
/// <param name="ReasonIndex">The reason at fault, by its position in the save, when one is.</param>
public sealed record SettingsError(string Code, string Message, string Field, int? ReasonIndex = null);
