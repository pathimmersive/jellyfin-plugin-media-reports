using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Relink;

/// <summary>
/// Daily, and by hand: moves issues whose title left the library to the title that replaced it,
/// and marks open issues whose file changed, for anything the library events missed.
/// </summary>
public sealed class RelinkTask : IScheduledTask
{
    private readonly LibraryRelink _relink;
    private readonly ILogger<RelinkTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelinkTask"/> class.
    /// </summary>
    /// <param name="relink">The re-linking service.</param>
    /// <param name="logger">The logger.</param>
    public RelinkTask(LibraryRelink relink, ILogger<RelinkTask> logger)
    {
        _relink = relink;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Re-link media reports";

    /// <inheritdoc />
    public string Key => "MediaReportsRelink";

    /// <inheritdoc />
    public string Description => "Moves reports on titles that left the library to the one title that replaced them, and marks open reports whose file changed for checking.";

    /// <inheritdoc />
    public string Category => Plugin.PluginName;

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var (moved, replaced) = _relink.Reconcile(cancellationToken);
        _logger.LogInformation("Re-link media reports: {Moved} issues moved, {Replaced} marked Replaced – verify", moved, replaced);
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }];
}
