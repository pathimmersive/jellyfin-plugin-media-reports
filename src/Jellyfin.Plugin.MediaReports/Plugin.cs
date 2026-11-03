using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaReports.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaReports;

/// <summary>
/// The Media Reports plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The name of the Media Reports page in the Dashboard, as its URL carries it.
    /// </summary>
    public const string PageName = "mediareports";

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

    /// <summary>
    /// Lists the Media Reports page under Plugins in the Dashboard menu, with its script.
    /// </summary>
    /// <returns>The page and its script.</returns>
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = PageName,
            DisplayName = PluginName,
            EmbeddedResourcePath = GetType().Namespace + ".Web.triage.html",
            EnableInMainMenu = true,
            MenuIcon = "report_problem",
        },
        new PluginPageInfo
        {
            Name = PageName + ".js",
            EmbeddedResourcePath = GetType().Namespace + ".Web.triage.js",
        },
    ];
}
