using System;
using Jellyfin.Plugin.MediaReports.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaReports;

/// <summary>
/// The Media Reports plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// The plugin's display name.
    /// </summary>
    public const string PluginName = "Media Reports";

    /// <summary>
    /// The plugin's fixed identifier. It must match the guid in meta.json and never change.
    /// </summary>
    public static readonly Guid PluginId = new("3a96edc1-4775-492e-ba6c-c0d8abc88662");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The server's application paths.</param>
    /// <param name="xmlSerializer">The server's XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Report playback and media problems to the server admin.";
}
