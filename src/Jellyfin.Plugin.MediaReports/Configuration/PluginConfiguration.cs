using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaReports.Configuration;

/// <summary>
/// The plugin's saved configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
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
}
