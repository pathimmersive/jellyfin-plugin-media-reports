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
}
