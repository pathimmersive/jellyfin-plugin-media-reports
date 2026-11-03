using System;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Notifications;
using Jellyfin.Plugin.MediaReports.Playback;
using Jellyfin.Plugin.MediaReports.Storage;
using Jellyfin.Plugin.MediaReports.Web;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
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
        serviceCollection.AddSingleton(provider => new ReportService(
            provider.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            provider.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
            provider.GetRequiredService<PlaybackCapture>(),
            provider.GetRequiredService<ReportStore>(),
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            TimeProvider.System));
        serviceCollection.AddSingleton(provider => new ReporterNotifier(
            provider.GetRequiredService<MediaBrowser.Controller.Session.ISessionManager>(),
            provider.GetRequiredService<ReportStore>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ReporterNotifier>>()));
        serviceCollection.AddSingleton(provider => new AdminService(
            provider.GetRequiredService<ReportStore>(),
            provider.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            provider.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
            provider.GetRequiredService<MediaBrowser.Controller.Library.IMediaSourceManager>(),
            provider.GetRequiredService<MediaBrowser.Controller.Providers.IProviderManager>(),
            provider.GetRequiredService<MediaBrowser.Model.IO.IFileSystem>(),
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            () => applicationHost.SystemId,
            provider.GetRequiredService<ReporterNotifier>()));
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserDeletedConsumer>();
        serviceCollection.AddTransient<IStartupFilter, IndexPageStartupFilter>();
    }
}
