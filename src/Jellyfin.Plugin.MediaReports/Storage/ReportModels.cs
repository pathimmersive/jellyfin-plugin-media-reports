using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaReports.Storage;

/// <summary>
/// Where an issue stands.
/// </summary>
public enum IssueStatus
{
    /// <summary>Every new issue.</summary>
    New,

    /// <summary>The admin is looking at it.</summary>
    Investigating,

    /// <summary>The title's file was replaced; set only by the plugin.</summary>
    ReplacedVerify,

    /// <summary>Closed: fixed.</summary>
    Fixed,

    /// <summary>Closed: won't be fixed.</summary>
    WontFix,

    /// <summary>Closed: a duplicate.</summary>
    Duplicate,

    /// <summary>Closed: couldn't be reproduced.</summary>
    CantReproduce,
}

/// <summary>
/// The result of a status change.
/// </summary>
public enum StatusChange
{
    /// <summary>The status changed.</summary>
    Changed,

    /// <summary>The issue already had that status.</summary>
    Unchanged,

    /// <summary>No such issue.</summary>
    NotFound,

    /// <summary>Reopening was refused because a newer issue for the same title and reason is open.</summary>
    NewerOpenIssue,
}

/// <summary>
/// The result of a withdrawal.
/// </summary>
public enum Withdrawal
{
    /// <summary>The report was removed.</summary>
    Withdrawn,

    /// <summary>No such report.</summary>
    NotFound,

    /// <summary>The report belongs to someone else.</summary>
    NotYours,

    /// <summary>The issue has left New, so the report stays.</summary>
    NotNew,
}

/// <summary>
/// Names and rules of the statuses.
/// </summary>
public static class IssueStatuses
{
    /// <summary>
    /// Gets a value indicating whether a status closes an issue.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>True for Fixed, Won't fix, Duplicate and Can't reproduce.</returns>
    public static bool IsClosed(this IssueStatus status) => status >= IssueStatus.Fixed;

    /// <summary>
    /// Gets the status's name as the admin sees it.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>The display name.</returns>
    public static string DisplayName(this IssueStatus status) => status switch
    {
        IssueStatus.ReplacedVerify => "Replaced – verify",
        IssueStatus.WontFix => "Won't fix",
        IssueStatus.CantReproduce => "Can't reproduce",
        _ => status.ToString(),
    };
}

/// <summary>
/// A report to store.
/// </summary>
public sealed record NewReport
{
    /// <summary>Gets the reporter.</summary>
    public Guid ReporterUserId { get; init; }

    /// <summary>Gets the reporter's name at report time.</summary>
    public string? ReporterName { get; init; }

    /// <summary>Gets the reported item.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the item type: Movie, Episode, Season or Series.</summary>
    public required string ItemType { get; init; }

    /// <summary>Gets the item name.</summary>
    public required string ItemName { get; init; }

    /// <summary>Gets the item's provider ids.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the series id, for an episode or season.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the series name.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the series' provider ids.</summary>
    public IReadOnlyDictionary<string, string> SeriesProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the season number.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the episode number.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets the file path at report time, shown only to admins.</summary>
    public string? FilePath { get; init; }

    /// <summary>Gets the file size at report time.</summary>
    public long? FileSize { get; init; }

    /// <summary>Gets the file's modified date at report time.</summary>
    public DateTimeOffset? FileModified { get; init; }

    /// <summary>Gets the reason code.</summary>
    public required string ReasonCode { get; init; }

    /// <summary>Gets the reporter's note.</summary>
    public string? Note { get; init; }

    /// <summary>Gets the playback diagnostics, absent when no playback was attached.</summary>
    public ReportSnapshot? Snapshot { get; init; }

    /// <summary>Gets the device name; defaults to the snapshot's.</summary>
    public string? Device { get; init; }
}

/// <summary>
/// The ids of a stored report and the issue it joined.
/// </summary>
/// <param name="ReportId">The report.</param>
/// <param name="IssueId">The issue.</param>
public sealed record FiledReport(long ReportId, long IssueId);

/// <summary>
/// The result of a status change.
/// </summary>
/// <param name="Result">What happened.</param>
/// <param name="ConflictingIssueId">For <see cref="StatusChange.NewerOpenIssue"/>, the open issue.</param>
/// <param name="Closed">True when the change moved an open issue into a closed status.</param>
public sealed record StatusChangeResult(StatusChange Result, long? ConflictingIssueId = null, bool Closed = false);

/// <summary>
/// One of a reporter's reports with where its issue stands.
/// </summary>
/// <param name="Report">The report.</param>
/// <param name="Status">The issue's status.</param>
/// <param name="StatusChangedAt">When the issue's status last changed, or null if never.</param>
/// <param name="ClosedAt">When the issue closed, or null while it is open.</param>
public sealed record ReporterReport(StoredReport Report, IssueStatus Status, DateTimeOffset? StatusChangedAt, DateTimeOffset? ClosedAt = null)
{
    /// <summary>
    /// Gets a value indicating whether the status changed after the reporter filed or last opened the report.
    /// </summary>
    public bool Unread => StatusChangedAt > (Report.ReporterSeenAt ?? Report.CreatedAt);

    /// <summary>
    /// Gets a value indicating whether the reporter removed it from their list and its status hasn't changed since.
    /// </summary>
    public bool Hidden => Report.ReporterHiddenAt is { } hidden && !(StatusChangedAt > hidden);
}

/// <summary>
/// The result of a reporter removing a report from their list.
/// </summary>
public enum Hiding
{
    /// <summary>The report is no longer listed.</summary>
    Hidden,

    /// <summary>No such report, or it is another user's.</summary>
    NotFound,

    /// <summary>The report's issue is still open.</summary>
    NotClosed,
}

/// <summary>
/// A stored report.
/// </summary>
public sealed record StoredReport
{
    /// <summary>Gets the id.</summary>
    public long Id { get; init; }

    /// <summary>Gets when it was filed.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the reporter, absent once the user was deleted.</summary>
    public Guid? ReporterUserId { get; init; }

    /// <summary>Gets the reporter's name, absent once the user was deleted.</summary>
    public string? ReporterName { get; init; }

    /// <summary>Gets a value indicating whether the reporter was deleted from the server.</summary>
    public bool IsDeletedUser => ReporterUserId is null;

    /// <summary>Gets the item id.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the item type.</summary>
    public string ItemType { get; init; } = string.Empty;

    /// <summary>Gets the item name.</summary>
    public string ItemName { get; init; } = string.Empty;

    /// <summary>Gets the item's provider ids.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the series id.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the series name.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the series' provider ids.</summary>
    public IReadOnlyDictionary<string, string> SeriesProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the season number.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the episode number.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets the file path at report time.</summary>
    public string? FilePath { get; init; }

    /// <summary>Gets the file size at report time.</summary>
    public long? FileSize { get; init; }

    /// <summary>Gets the file's modified date at report time.</summary>
    public DateTimeOffset? FileModified { get; init; }

    /// <summary>Gets the reason code.</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Gets the note.</summary>
    public string? Note { get; init; }

    /// <summary>Gets the issue.</summary>
    public long IssueId { get; init; }

    /// <summary>Gets the diagnostics snapshot as stored.</summary>
    public string? SnapshotJson { get; init; }

    /// <summary>Gets the play method.</summary>
    public string? PlayMethod { get; init; }

    /// <summary>Gets the transcode reasons, comma separated.</summary>
    public string? TranscodeReasons { get; init; }

    /// <summary>Gets the client.</summary>
    public string? Client { get; init; }

    /// <summary>Gets the device name.</summary>
    public string? Device { get; init; }

    /// <summary>Gets when the reporter last saw the issue's status.</summary>
    public DateTimeOffset? ReporterSeenAt { get; init; }

    /// <summary>Gets when the reporter removed the report from their list, if they did.</summary>
    public DateTimeOffset? ReporterHiddenAt { get; init; }
}

/// <summary>
/// A stored issue.
/// </summary>
public sealed record StoredIssue
{
    /// <summary>Gets the id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the item it groups.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the reason it groups.</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Gets the status.</summary>
    public IssueStatus Status { get; init; }

    /// <summary>Gets the admin's private notes.</summary>
    public string? AdminNotes { get; init; }

    /// <summary>Gets when it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets when it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets when it closed, absent while open.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Gets the size of the file it was last checked against.</summary>
    public long? CheckedFileSize { get; init; }

    /// <summary>Gets the modified date of the file it was last checked against.</summary>
    public DateTimeOffset? CheckedFileModified { get; init; }
}

/// <summary>
/// One line of an issue's history.
/// </summary>
public sealed record StoredEvent
{
    /// <summary>Gets the id.</summary>
    public long Id { get; init; }

    /// <summary>Gets when it happened.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Gets the issue.</summary>
    public long? IssueId { get; init; }

    /// <summary>Gets the report.</summary>
    public long? ReportId { get; init; }

    /// <summary>Gets who did it, absent for the plugin itself or a deleted user.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Gets the kind: filed, withdrawn, status changed, note added, action run, relinked, purged or anonymised.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets a short detail.</summary>
    public string? Detail { get; init; }
}
