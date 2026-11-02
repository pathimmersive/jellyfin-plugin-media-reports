using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Storage;

/// <summary>
/// Deletes closed reports once they are older than the retention period, daily.
/// </summary>
public sealed class PurgeClosedReportsTask : IScheduledTask
{
    private readonly ReportStore _store;
    private readonly ILogger<PurgeClosedReportsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PurgeClosedReportsTask"/> class.
    /// </summary>
    /// <param name="store">The report store.</param>
    /// <param name="logger">The logger.</param>
    public PurgeClosedReportsTask(ReportStore store, ILogger<PurgeClosedReportsTask> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Purge closed media reports";

    /// <inheritdoc />
    public string Key => "MediaReportsPurge";

    /// <inheritdoc />
    public string Description => "Deletes closed media reports older than the retention period set in Media Reports. Open reports are never deleted.";

    /// <inheritdoc />
    public string Category => Plugin.PluginName;

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var purged = _store.Purge();
        _logger.LogInformation("Purged {Count} closed media report issues", purged);
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }];
}

/// <summary>
/// Writes the daily consistent copy of the reports database, media-reports.backup.db.
/// </summary>
public sealed class CopyReportsDatabaseTask : IScheduledTask
{
    private readonly ReportStore _store;
    private readonly ILogger<CopyReportsDatabaseTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CopyReportsDatabaseTask"/> class.
    /// </summary>
    /// <param name="store">The report store.</param>
    /// <param name="logger">The logger.</param>
    public CopyReportsDatabaseTask(ReportStore store, ILogger<CopyReportsDatabaseTask> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Copy media reports database";

    /// <inheritdoc />
    public string Key => "MediaReportsCopy";

    /// <inheritdoc />
    public string Description => "Writes a consistent copy of the Media Reports database to media-reports.backup.db, for file backups to take.";

    /// <inheritdoc />
    public string Category => Plugin.PluginName;

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _store.WriteCopy();
        _logger.LogInformation("Wrote {Path}", _store.Database.CopyPath);
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks }];
}
