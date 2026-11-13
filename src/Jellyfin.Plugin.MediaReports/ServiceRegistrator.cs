using System;
using System.Net.Http;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Notifications;
using Jellyfin.Plugin.MediaReports.Playback;
using Jellyfin.Plugin.MediaReports.Relink;
using Jellyfin.Plugin.MediaReports.Storage;
using Jellyfin.Plugin.MediaReports.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Activity;
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
        serviceCollection.AddSingleton(provider => new LibraryRelink(
            provider.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            new JellyfinRelinkLibrary(provider.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>()),
            provider.GetRequiredService<ReportStore>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LibraryRelink>>()));
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<LibraryRelink>());
        serviceCollection.AddSingleton(provider => new ReportService(
            provider.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            provider.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
            provider.GetRequiredService<PlaybackCapture>(),
            provider.GetRequiredService<ReportStore>(),
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            TimeProvider.System,
            report =>
            {
                provider.GetRequiredService<ReportMetrics>().Filed(report);
                provider.GetRequiredService<ReportAlerts>().Filed(report);
            }));
        serviceCollection.AddSingleton(provider => new ReportMetrics(
            provider.GetRequiredService<ReportStore>(),
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0"));
        serviceCollection.AddSingleton(provider => new ReportAlerts(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient(NamedClient.Default),
            provider.GetRequiredService<IActivityManager>(),
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            () => ServerUrl(applicationHost, provider.GetRequiredService<IConfigurationManager>()),
            TimeProvider.System,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ReportAlerts>>()));
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
        serviceCollection.AddSingleton(provider => new SettingsService(
            provider.GetRequiredService<ReportStore>(),
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            config => Plugin.Instance?.UpdateConfiguration(config),
            () => ServerUrl(applicationHost, provider.GetRequiredService<IConfigurationManager>())));
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserDeletedConsumer>();
        serviceCollection.AddTransient<IStartupFilter, IndexPageStartupFilter>();
    }

    // The published address for every client (or for external ones) when one is set, else the local one.
    private static string ServerUrl(IServerApplicationHost host, IConfigurationManager configuration)
    {
        foreach (var entry in configuration.GetNetworkConfiguration().PublishedServerUriBySubnet ?? [])
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].ToLowerInvariant() is "all" or "external" or "0.0.0.0/0"
                && Uri.TryCreate(parts[1], UriKind.Absolute, out var uri))
            {
                return uri.ToString().TrimEnd('/');
            }
        }

        return host.GetApiUrlForLocalAccess(null, true);
    }
}
