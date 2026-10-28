using Jellyfin.Plugin.MediaReports.Playback;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaReports;

/// <summary>
/// Registers the plugin's services with the server.
/// </summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PlaybackCapture>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<PlaybackCapture>());
    }
}
