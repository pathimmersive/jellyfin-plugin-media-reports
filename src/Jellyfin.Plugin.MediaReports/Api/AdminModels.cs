using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// The Reports tab's summary line.
/// </summary>
/// <param name="OpenIssues">How many issues are open.</param>
/// <param name="Reports">How many reports the open issues hold.</param>
/// <param name="New">How many open issues have a report newer than the caller last looked.</param>
/// <param name="ButtonStopped">True when the Report button hasn't loaded in any app for 7 days while web apps played.</param>
public sealed record AdminSummary(int OpenIssues, int Reports, int New, bool ButtonStopped = false);

/// <summary>
/// What the admin's issue list shows.
/// </summary>
public sealed record IssueQuery
{
    /// <summary>Gets the view: open, closed or all.</summary>
    public string View { get; init; } = "open";

    /// <summary>Gets the grouping: title or titleClient.</summary>
    public string GroupBy { get; init; } = "title";

    /// <summary>Gets the reason code to show only, or null.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the client to show only, or null.</summary>
    public string? Client { get; init; }

    /// <summary>Gets the title text to search for, or null.</summary>
    public string? Q { get; init; }

    /// <summary>Gets the sort: title, reports, status or last.</summary>
    public string Sort { get; init; } = "last";

    /// <summary>Gets the page, from 1.</summary>
    public int Page { get; init; } = 1;
}

/// <summary>
/// One page of the admin's issue list.
/// </summary>
public sealed record IssueList
{
    /// <summary>Gets the rows of this page.</summary>
    public IReadOnlyList<IssueRow> Rows { get; init; } = [];

    /// <summary>Gets how many rows there are on every page together.</summary>
    public int Total { get; init; }

    /// <summary>Gets the page.</summary>
    public int Page { get; init; }

    /// <summary>Gets the rows on a page.</summary>
    public int PageSize { get; init; }

    /// <summary>Gets the reasons present on issues of the view, for the filter.</summary>
    public IReadOnlyList<ReasonOption> Reasons { get; init; } = [];

    /// <summary>Gets the clients present in reports of the view, for the filter.</summary>
    public IReadOnlyList<string> Clients { get; init; } = [];
}

/// <summary>
/// A play method and how many reports used it.
/// </summary>
/// <param name="Method">Direct play, direct stream or transcode.</param>
/// <param name="Count">How many reports.</param>
public sealed record PlayMethodCount(string Method, int Count);

/// <summary>
/// A row of the admin's issue list: an issue, an issue and one client, or a season holding episode issues.
/// </summary>
public sealed record IssueRow
{
    /// <summary>Gets the kind: issue or season.</summary>
    public string Kind { get; init; } = "issue";

    /// <summary>Gets the issue, absent for a season row.</summary>
    public long? IssueId { get; init; }

    /// <summary>Gets the item, absent for a season row.</summary>
    public Guid? ItemId { get; init; }

    /// <summary>Gets the item type: Movie, Episode, Season or Series.</summary>
    public string ItemType { get; init; } = string.Empty;

    /// <summary>Gets the title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the line beneath the title: season and episode, or year.</summary>
    public string? Line { get; init; }

    /// <summary>Gets the reason's code.</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Gets the reason's current label.</summary>
    public string ReasonLabel { get; init; } = string.Empty;

    /// <summary>Gets how many reports.</summary>
    public int Reports { get; init; }

    /// <summary>Gets how many different people reported.</summary>
    public int People { get; init; }

    /// <summary>Gets the clients the reports came from.</summary>
    public IReadOnlyList<string> Clients { get; init; } = [];

    /// <summary>Gets the client of a by-title-and-client row; null for reports with no playback.</summary>
    public string? Client { get; init; }

    /// <summary>Gets how the reports played.</summary>
    public IReadOnlyList<PlayMethodCount> PlayMethods { get; init; } = [];

    /// <summary>Gets the status, or Mixed for a season row whose episodes differ.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the status as the admin reads it.</summary>
    public string StatusLabel { get; init; } = string.Empty;

    /// <summary>Gets when the last report came in.</summary>
    public DateTimeOffset LastReport { get; init; }

    /// <summary>Gets a value indicating whether a report is newer than the caller last looked.</summary>
    public bool IsNew { get; init; }

    /// <summary>Gets the series of a season row.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the season number of a season row.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets how many episodes a season row holds.</summary>
    public int? EpisodeCount { get; init; }

    /// <summary>Gets a season row's episode issues.</summary>
    public IReadOnlyList<IssueRow> Children { get; init; } = [];
}

/// <summary>
/// One issue, as the admin opens it.
/// </summary>
public sealed record IssueDetail
{
    /// <summary>Gets the issue.</summary>
    public long Id { get; init; }

    /// <summary>Gets the item.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the item type.</summary>
    public string ItemType { get; init; } = string.Empty;

    /// <summary>Gets the title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the line beneath the title.</summary>
    public string? Line { get; init; }

    /// <summary>Gets the reason's code.</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Gets the reason's current label.</summary>
    public string ReasonLabel { get; init; } = string.Empty;

    /// <summary>Gets the status.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the status as the admin reads it.</summary>
    public string StatusLabel { get; init; } = string.Empty;

    /// <summary>Gets the admin's private notes.</summary>
    public string? Notes { get; init; }

    /// <summary>Gets when it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets when it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets when it closed.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Gets how many different people reported.</summary>
    public int People { get; init; }

    /// <summary>Gets the reports, newest first.</summary>
    public IReadOnlyList<IssueReport> Reports { get; init; } = [];

    /// <summary>Gets the history, oldest first.</summary>
    public IReadOnlyList<IssueEvent> History { get; init; } = [];

    /// <summary>Gets the title as Jellyfin has it now.</summary>
    public FileDetails File { get; init; } = new();

    /// <summary>Gets the server id, for the link to the title in Jellyfin.</summary>
    public string ServerId { get; init; } = string.Empty;
}

/// <summary>
/// One report in an issue.
/// </summary>
public sealed record IssueReport
{
    /// <summary>Gets the report.</summary>
    public long Id { get; init; }

    /// <summary>Gets when it was filed.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the reporter's name; null once the user was deleted.</summary>
    public string? ReporterName { get; init; }

    /// <summary>Gets the device.</summary>
    public string? Device { get; init; }

    /// <summary>Gets the client.</summary>
    public string? Client { get; init; }

    /// <summary>Gets the play method.</summary>
    public string? PlayMethod { get; init; }

    /// <summary>Gets the transcode reasons.</summary>
    public IReadOnlyList<string> TranscodeReasons { get; init; } = [];

    /// <summary>Gets the position reached, in ticks.</summary>
    public long? PositionTicks { get; init; }

    /// <summary>Gets the reporter's note.</summary>
    public string? Note { get; init; }

    /// <summary>Gets the full playback snapshot, or null when none was attached.</summary>
    public JsonElement? Snapshot { get; init; }
}

/// <summary>
/// One line of an issue's history.
/// </summary>
/// <param name="At">When.</param>
/// <param name="Kind">What: filed, withdrawn, status changed, note added, action run or relinked.</param>
/// <param name="Detail">A short detail.</param>
/// <param name="ActorName">Who; null for Media Reports itself or a deleted user.</param>
public sealed record IssueEvent(DateTimeOffset At, string Kind, string? Detail, string? ActorName);

/// <summary>
/// The title's file or folder as Jellyfin has it now.
/// </summary>
public sealed record FileDetails
{
    /// <summary>Gets a value indicating whether the title is still in the library.</summary>
    public bool InLibrary { get; init; }

    /// <summary>Gets the file or folder path; when gone, the path stored with the newest report.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the file size in bytes.</summary>
    public long? Size { get; init; }

    /// <summary>Gets when it was added to the library.</summary>
    public DateTimeOffset? DateAdded { get; init; }

    /// <summary>Gets the container.</summary>
    public string? Container { get; init; }

    /// <summary>Gets the overall bitrate.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Gets the streams.</summary>
    public IReadOnlyList<StreamDetails> Streams { get; init; } = [];

    /// <summary>Gets how many episodes a season or series holds.</summary>
    public int? EpisodeCount { get; init; }
}

/// <summary>
/// A stream of the title's file.
/// </summary>
public sealed record StreamDetails
{
    /// <summary>Gets the type: Video, Audio or Subtitle.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets the codec.</summary>
    public string? Codec { get; init; }

    /// <summary>Gets the profile.</summary>
    public string? Profile { get; init; }

    /// <summary>Gets the width.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the height.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the video range: SDR, HDR10, Dolby Vision and so on.</summary>
    public string? VideoRange { get; init; }

    /// <summary>Gets the frame rate.</summary>
    public float? FrameRate { get; init; }

    /// <summary>Gets the language.</summary>
    public string? Language { get; init; }

    /// <summary>Gets Jellyfin's display title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets a value indicating whether it is the default stream.</summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// A change to one issue.
/// </summary>
/// <param name="Status">The new status, or null to keep it.</param>
/// <param name="Notes">The new notes, or null to keep them; empty clears them.</param>
public sealed record IssuePatch(string? Status, string? Notes);

/// <summary>
/// One status for several issues.
/// </summary>
/// <param name="IssueIds">The issues.</param>
/// <param name="Status">The new status.</param>
public sealed record IssueStatusRequest(IReadOnlyList<long> IssueIds, string Status);

/// <summary>
/// A refusal that names the issues it is about.
/// </summary>
/// <param name="Code">The error code.</param>
/// <param name="Message">A short explanation.</param>
/// <param name="IssueId">The issue the refusal names, such as the newer open issue.</param>
/// <param name="IssueIds">The issues that were refused.</param>
public sealed record IssueError(string Code, string Message, long? IssueId = null, IReadOnlyList<long>? IssueIds = null);
