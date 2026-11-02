using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Storage;

/// <summary>
/// Opens and migrates the reports database when the server starts.
/// </summary>
public sealed class StorageStartup : IHostedService
{
    private readonly ReportDatabase _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageStartup"/> class.
    /// </summary>
    /// <param name="db">The database.</param>
    public StorageStartup(ReportDatabase db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _db.Initialize();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Keeps a deleted user's reports but clears who they were.
/// </summary>
public sealed class UserDeletedConsumer : IEventConsumer<UserDeletedEventArgs>
{
    private readonly ReportStore _store;
    private readonly ILogger<UserDeletedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDeletedConsumer"/> class.
    /// </summary>
    /// <param name="store">The report store.</param>
    /// <param name="logger">The logger.</param>
    public UserDeletedConsumer(ReportStore store, ILogger<UserDeletedConsumer> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        try
        {
            var count = _store.AnonymiseUser(eventArgs.Argument.Id);
            _logger.LogInformation("Anonymised {Count} media reports of a deleted user", count);
        }
        catch (StorageUnavailableException)
        {
            _logger.LogError("A user was deleted while the Media Reports database was unavailable; their reports were not anonymised");
        }

        return Task.CompletedTask;
    }
}
