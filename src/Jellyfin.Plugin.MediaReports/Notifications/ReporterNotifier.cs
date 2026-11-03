using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Notifications;

/// <summary>
/// Tells reporters their report was closed, with a toast in each of their connected apps.
/// A send is a courtesy: it is never confirmed, retried or queued; My Reports is the record.
/// </summary>
public sealed class ReporterNotifier
{
    /// <summary>
    /// How long the toast stays on screen. A message without a timeout is an alert that needs a tap.
    /// </summary>
    public const long TimeoutMs = 8000;

    private readonly ISessionManager _sessions;
    private readonly ReportStore _store;
    private readonly ILogger<ReporterNotifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReporterNotifier"/> class.
    /// </summary>
    /// <param name="sessions">The session manager.</param>
    /// <param name="store">The report store.</param>
    /// <param name="logger">The logger.</param>
    public ReporterNotifier(ISessionManager sessions, ReportStore store, ILogger<ReporterNotifier> logger)
    {
        _sessions = sessions;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Sends each reporter in the issues one notice of the issue's closed status. Call it
    /// only after the change is stored, and only for issues the change closed. Never throws.
    /// </summary>
    /// <param name="issueIds">The issues just closed.</param>
    /// <returns>A task that completes when every send has been attempted.</returns>
    public Task NotifyClosedAsync(IEnumerable<long> issueIds)
    {
        var sends = new List<Task>();
        try
        {
            foreach (var issueId in issueIds)
            {
                if (_store.GetIssue(issueId) is not { } issue || !issue.Status.IsClosed())
                {
                    continue;
                }

                var reporters = _store.GetIssueReports(issueId)
                    .Where(r => r.ReporterUserId is not null)
                    .GroupBy(r => r.ReporterUserId!.Value);
                foreach (var reporter in reporters)
                {
                    var message = new MessageCommand
                    {
                        Header = "Media Reports",
                        Text = $"Your report on {ReporterText.Title(reporter.First())} is {ReporterText.StatusLabel(issue.Status)}.",
                        TimeoutMs = TimeoutMs,
                    };
                    sends.AddRange(_sessions.Sessions
                        .Where(s => s.UserId.Equals(reporter.Key) && s.SupportsRemoteControl)
                        .Select(s => Send(s.Id, message)));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't work out who to tell that issues {Issues} closed", string.Join(", ", issueIds));
        }

        return Task.WhenAll(sends);
    }

    private async Task Send(string sessionId, MessageCommand message)
    {
        try
        {
            await _sessions.SendMessageCommand(null!, sessionId, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't send the report notice to session {Session}", sessionId);
        }
    }
}
