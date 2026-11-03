using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.MediaReports.Storage;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// How a report reads to the person who filed it: its title and its status.
/// </summary>
public static class ReporterText
{
    /// <summary>
    /// Gets the title a reporter sees: the item's name, an episode as "Series S01E02",
    /// a season as "Series Season 1".
    /// </summary>
    /// <param name="report">The report.</param>
    /// <returns>The title.</returns>
    public static string Title(StoredReport report) => report.ItemType switch
    {
        "Episode" when report.SeasonNumber is { } s && report.EpisodeNumber is { } e =>
            string.Create(CultureInfo.InvariantCulture, $"{report.SeriesName ?? report.ItemName} S{s:00}E{e:00}"),
        "Season" when report.SeriesName is not null => $"{report.SeriesName} {report.ItemName}",
        _ => report.ItemName,
    };

    /// <summary>
    /// Gets the status label a reporter sees. Anything the admin is still working on reads "Being looked at".
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>The label.</returns>
    public static string StatusLabel(IssueStatus status) => status switch
    {
        IssueStatus.Investigating or IssueStatus.ReplacedVerify => "Being looked at",
        _ => status.DisplayName(),
    };

    /// <summary>
    /// Gets how a closed report's expiry reads: whole months, rounded, from 60 days up;
    /// days below that; "Expires today" under one day.
    /// </summary>
    /// <param name="expiresAt">When the report is purged.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The label.</returns>
    public static string ExpiryLabel(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var days = (expiresAt - now).TotalDays;
        if (days < 1)
        {
            return "Expires today";
        }

        if (days < 60)
        {
            var whole = (int)Math.Ceiling(days);
            return whole == 1 ? "Expires in 1 day" : string.Create(CultureInfo.InvariantCulture, $"Expires in {whole} days");
        }

        var months = (int)Math.Round(days / 30.4375, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"Expires in {months} months");
    }

    /// <summary>
    /// Reads the status a "status changed" event moved to, from its "Old → New" detail.
    /// </summary>
    /// <param name="detail">The event's detail.</param>
    /// <returns>The new status, or null when the detail doesn't name one.</returns>
    public static IssueStatus? StatusChangedTo(string? detail)
    {
        var name = detail?.Split(" → ")[^1];
        return Enum.GetValues<IssueStatus>().Cast<IssueStatus?>().FirstOrDefault(s => string.Equals(s!.Value.DisplayName(), name, StringComparison.Ordinal));
    }
}
