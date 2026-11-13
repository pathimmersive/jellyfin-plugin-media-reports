using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Notifications;
using Jellyfin.Plugin.MediaReports.Storage;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// The plugin's counts for Prometheus. Labels carry reason codes, statuses and client
/// names only: never a title, file path, user name, device name or address.
/// </summary>
public sealed class ReportMetrics
{
    /// <summary>
    /// The content type of the Prometheus text format.
    /// </summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private readonly ReportStore _store;
    private readonly Func<PluginConfiguration> _config;
    private readonly string _version;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(string Reason, string Client), long> _filed = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportMetrics"/> class.
    /// </summary>
    /// <param name="store">The report store, read on every scrape.</param>
    /// <param name="config">Returns the current configuration.</param>
    /// <param name="version">The plugin's version.</param>
    /// <param name="time">The clock; the system clock when null.</param>
    public ReportMetrics(ReportStore store, Func<PluginConfiguration> config, string version, TimeProvider? time = null)
    {
        _store = store;
        _config = config;
        _version = version;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Counts a report filed since the server started.
    /// </summary>
    /// <param name="report">The stored report.</param>
    public void Filed(StoredReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _filed.AddOrUpdate((report.ReasonCode, report.Client ?? string.Empty), 1, (_, n) => n + 1);
    }

    /// <summary>
    /// Renders every series in the Prometheus text format, reading the database now. When the
    /// database didn't open, only the series that need no database are given, with storage down.
    /// </summary>
    /// <returns>The text.</returns>
    public string Render()
    {
        IReadOnlyList<(string Reason, string Status, int Count)>? issues = null;
        var openReports = 0L;
        IReadOnlyList<ClientSeen> clients = [];
        ButtonStatus? button = null;
        try
        {
            issues = _store.GetOpenIssueCounts();
            openReports = _store.GetOpenReportCount();
            clients = _store.GetClientsSeen();
            button = ButtonWatch.Evaluate(_store, _config(), _time.GetUtcNow());
        }
        catch (StorageUnavailableException)
        {
            issues = null;
        }

        var text = new StringBuilder();
        Family(text, "media_reports_reports_filed_total", "counter", "Reports filed since the server started, by reason code and client.");
        foreach (var ((reason, client), count) in _filed.OrderBy(f => f.Key.Reason, StringComparer.Ordinal).ThenBy(f => f.Key.Client, StringComparer.Ordinal))
        {
            Sample(text, "media_reports_reports_filed_total", count, ("reason", reason), ("client", client));
        }

        if (issues is not null)
        {
            Family(text, "media_reports_open_issues", "gauge", "Open issues by reason code and status.");
            foreach (var (reason, status, count) in issues)
            {
                Sample(text, "media_reports_open_issues", count, ("reason", reason), ("status", status));
            }

            Family(text, "media_reports_open_reports", "gauge", "Reports in open issues.");
            Sample(text, "media_reports_open_reports", openReports);

            Family(text, "media_reports_button_stopped", "gauge", "1 when the Report button hasn't loaded in any app for 7 days while web apps played.");
            Sample(text, "media_reports_button_stopped", button?.Stopped == true ? 1 : 0);
            Family(text, "media_reports_button_last_load_timestamp_seconds", "gauge", "When the Report button last loaded, by client, as Unix seconds.");
            foreach (var client in clients.Where(c => c.LastLoad is not null))
            {
                Sample(text, "media_reports_button_last_load_timestamp_seconds", client.LastLoad!.Value.ToUnixTimeSeconds(), ("client", client.Client));
            }
        }

        Family(text, "media_reports_reporting_enabled", "gauge", "1 when users can report problems.");
        Sample(text, "media_reports_reporting_enabled", _config().ReportingEnabled ? 1 : 0);
        Family(text, "media_reports_storage_up", "gauge", "1 when the Media Reports database is open.");
        Sample(text, "media_reports_storage_up", issues is null ? 0 : 1);
        Family(text, "media_reports_info", "gauge", "The plugin's version.");
        Sample(text, "media_reports_info", 1, ("version", _version));
        return text.ToString();
    }

    private static void Family(StringBuilder text, string name, string type, string help) =>
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
            .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

    private static void Sample(StringBuilder text, string name, long value, params (string Name, string Value)[] labels)
    {
        text.Append(name);
        if (labels.Length > 0)
        {
            text.Append('{').AppendJoin(',', labels.Select(l => $"{l.Name}=\"{Escape(l.Value)}\"")).Append('}');
        }

        text.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    // The text format escapes a backslash, a double quote and a line feed in label values.
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
