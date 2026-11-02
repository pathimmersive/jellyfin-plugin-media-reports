using System;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.MediaReports.Playback;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Events;
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

        serviceCollection.AddSingleton<ReportDatabase>();
        serviceCollection.AddSingleton(provider => new ReportStore(
            provider.GetRequiredService<ReportDatabase>(),
            TimeProvider.System,
            () => Plugin.Instance?.Configuration.ReportRetentionDays ?? 365));
        serviceCollection.AddHostedService<StorageStartup>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserDeletedConsumer>();
    }
}
